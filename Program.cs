using System.Diagnostics;
using System.Reflection;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace LocalDpi;

internal sealed class Preferences
{
    public bool English { get; set; }
    public bool AllSites { get; set; }
    public bool[] Enabled { get; set; } = [true, true, true];
    public string[] Hosts { get; set; } = [
        "www.youtube.com\nyoutu.be\ni.ytimg.com\nredirector.googlevideo.com\ngooglevideo.com",
        "discord.com\ngateway.discord.gg\ncdn.discordapp.com\nupdates.discord.com",
        "chatgpt.com\nclaude.ai\ngemini.google.com\ngrok.com"
    ];
    public List<Profile> Saved { get; set; } = [];
    public Dictionary<string, Profile> Networks { get; set; } = [];
}

internal sealed class Dashboard : Window
{
    private static readonly Profile[] Presets = [
        new("Split / SNI", "multisplit", "1,midsld"),
        new("Disorder / SNI", "multidisorder", "midsld"),
        new("Split / 2", "multisplit", "2"),
        new("Fake split / SNI", "fakedsplit", "midsld"),
        new("Combo / TLS + QUIC + Discord", Engine.Combo, "")
    ];
    private static readonly string[] Services = ["YouTube", "Discord", "AI"];
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalDpi");
    private static readonly string Config = Path.Combine(Root, "settings.json");
    private readonly Engine engine = new(Path.Combine(Root, "engine-v1.0.7"));
    private readonly Preferences prefs;
    private readonly ComboBox profiles = new() { MinWidth = 210 };
    private readonly StackPanel navigation = new();
    private readonly StackPanel page = new();
    private readonly TextBlock badge = new();
    private readonly TextBox report = Output();
    private readonly TextBox journal = Output();
    private readonly Forms.NotifyIcon tray;
    private readonly List<Button> actionButtons = [];
    private CancellationTokenSource? task;
    private Button? cancelButton;
    private Button? saveButton;
    private Button? toggleButton;
    private Profile? winner;
    private string? winningNetwork;
    private string? winningScope;
    private Profile? activeProfile;
    private string? activeNetwork;
    private bool all;
    private bool quit;
    private int tab;
    private static readonly Brush Panel = BrushOf("#272727");
    private static readonly Brush Ink = BrushOf("#F5F5F5");
    private static readonly Brush Muted = BrushOf("#BDBDBD");
    private static readonly Brush Accent = BrushOf("#CDCDCD");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--install-engine", var installLog])
        {
            try
            {
                using var installer = new Engine(Path.Combine(Root, "engine-v1.0.7"));
                installer.Install(CancellationToken.None).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(installLog, ex.Message);
                return 1;
            }
        }
        using var instance = new Mutex(true, @"Local\LocalDpi." + WindowsIdentity.GetCurrent().User?.Value, out bool first);
        if (!first && !args.Contains("--smoke-test"))
        {
            if (!args.Contains("--takeover")) return 0;
            try { if (!instance.WaitOne(TimeSpan.FromSeconds(10))) return 2; }
            catch (AbandonedMutexException) { }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Dashboard();
        app.MainWindow = window;
        if (args.Contains("--tray")) window.Loaded += (_, _) => window.Hide();
        if (args is ["--smoke-test", var enginePath])
            window.Loaded += async (_, _) =>
            {
                int result = 0;
                using var tested = new Engine(enginePath);
                try
                {
                    var mixed = new Profile("Mixed", "multisplit", "2",
                        [new ProfileRule("multidisorder", "midsld", ["www.youtube.com"]),
                         new ProfileRule("fakedsplit", "midsld", ["discord.com"])]);
                    string[] targets = ["www.youtube.com", "discord.com"];
                    var combo = Engine.BuildArguments(Presets.Single(profile => profile.Function == Engine.Combo), targets, false).ToArray();
                    var youtubeOnly = Engine.BuildArguments(Presets.Single(profile => profile.Function == Engine.Combo), ["www.youtube.com"], false).ToArray();
                    if (!combo.Contains("--wf-udp-out=443,19294-19344,50000-65535") ||
                        !combo.Contains("--filter-udp=19294-19344,50000-65535") ||
                        !combo.Contains("--filter-l7=discord,stun") ||
                        !combo.Contains("--hostlist-domains=discord.com,discord.media") ||
                        youtubeOnly.Contains("--filter-l7=discord,stun"))
                        throw new InvalidOperationException("Invalid Discord voice profile");
                    var broad = Engine.BuildArguments(mixed, targets, true).ToArray();
                    var narrow = Engine.BuildArguments(mixed, targets, false).ToArray();
                    if (broad.Count(arg => arg == "--new") != 2 || narrow.Count(arg => arg == "--new") != 1 ||
                        broad[^1] != "--lua-desync=multisplit:pos=2" ||
                        !narrow.Contains("--hostlist-domains=discord.com") || !narrow.Contains("--hostlist-domains=www.youtube.com"))
                        throw new InvalidOperationException("Invalid mixed profile arguments");
                    string[] video = ["googlevideo.com", "redirector.googlevideo.com", "discord.com"];
                    if (!ProbeTargets(video).SequenceEqual(video.Skip(1)) ||
                        !Engine.BuildArguments(Presets[0], video, false).Any(arg =>
                            arg == "--hostlist-domains=discord.com,googlevideo.com,redirector.googlevideo.com"))
                        throw new InvalidOperationException("Video probe and routing host lists differ unexpectedly");
                    if (WindowsIdentity.GetCurrent() is { } identity &&
                        new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    {
                        foreach (var profile in Presets)
                            await tested.Start(profile, ["www.youtube.com", "discord.com", "chatgpt.com"], false, true, CancellationToken.None);
                        await tested.Start(mixed, targets, true, true, CancellationToken.None);
                    }
                    window.tab = 4;
                    window.Draw();
                    window.tab = 0;
                    window.Draw();
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(enginePath)!, "smoke-error.txt"), ex.ToString());
                    result = 1;
                }
                window.quit = true;
                app.Shutdown(result);
            };
        return app.Run(window);
    }

    private Dashboard()
    {
        Directory.CreateDirectory(Root);
        try { prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Config)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { prefs = new(); }
        all = prefs.AllSites;
        Title = "Local DPI";
        var iconUri = new Uri("pack://application:,,,/LocalDpi;component/LocalDpi.ico");
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
        Width = 1060;
        Height = 755;
        MinWidth = 740;
        MinHeight = 530;
        Background = BrushOf("#1C1C1C");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/LocalDpi;component/DarkTheme.xaml", UriKind.Relative) });
        Foreground = Ink;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        navigation.Margin = new Thickness(14, 20, 12, 12);
        layout.Children.Add(navigation);
        page.Margin = new Thickness(24, 20, 24, 24);
        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        layout.Children.Add(scroll);
        Content = layout;

        using var iconStream = Application.GetResourceStream(iconUri).Stream;
        using var iconResource = new System.Drawing.Icon(iconStream, 32, 32);
        var trayIcon = (System.Drawing.Icon)iconResource.Clone();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = Title, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Local DPI", null, (_, _) => Restore());
        menu.Items.Add("Exit / Выход", null, (_, _) => { quit = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Restore();
        Closing += (_, e) => { if (!quit) { e.Cancel = true; Hide(); } else task?.Cancel(); };
        Closed += (_, _) => { tray.Dispose(); trayIcon.Dispose(); engine.Dispose(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Status();
        timer.Start();
        Closed += (_, _) => timer.Stop();
        Draw();
    }

    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private string L(string ru, string en) => prefs.English ? en : ru;
    private static Brush BrushOf(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private static TextBox Output() => new() {
        IsReadOnly = true, MinHeight = 160, MaxHeight = 310, Background = BrushOf("#202020"),
        Foreground = Ink, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(10),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private void Persist()
    {
        string staged = Config + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(staged, Config, overwrite: true);
    }

    private void Draw()
    {
        if (report.Parent is StackPanel rp) rp.Children.Remove(report);
        if (journal.Parent is StackPanel jp) jp.Children.Remove(journal);
        if (profiles.Parent is StackPanel cp) cp.Children.Remove(profiles);
        if (badge.Parent is StackPanel bp) bp.Children.Remove(badge);
        page.Children.Clear();
        navigation.Children.Clear();
        actionButtons.Clear();
        cancelButton = saveButton = toggleButton = null;
        var names = new[] { L("Главная", "Home"), L("Стратегии", "Strategies"), L("Хостлисты", "Host lists"),
            L("Диагностика", "Diagnostics"), L("Журнал", "Log") };
        Label(navigation, "LOCAL DPI", 22, Accent);
        for (int index = 0; index < names.Length; index++)
        {
            int next = index;
            Add(navigation, names[index], () => { tab = next; Draw(); }, primary: tab == index);
        }
        Add(navigation, "RU / EN", () => { prefs.English = !prefs.English; Persist(); Draw(); });
        string? repository = typeof(Dashboard).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UpdateRepository")?.Value;
        if (!string.IsNullOrWhiteSpace(repository))
            Add(navigation, L("Обновления", "Updates"), () => Process.Start(new ProcessStartInfo(
                "https://github.com/" + repository + "/releases/latest") { UseShellExecute = true }));
        Label(page, names[tab], 31);
        switch (tab)
        {
            case 0: Home(); break;
            case 1: Strategies(); break;
            case 2: Hosts(); break;
            case 3:
                Label(page, L("Прямые TLS-рукопожатия с SNI. Результат сам по себе не определяет причину блокировки.",
                    "Direct TLS handshakes with SNI. Results alone cannot identify the cause of interference."), 14, Muted);
                Action(page, L("Проверить без обхода", "Check without bypass"), ct => Scan(false, false, ct));
                Add(page, L("Открыть настройки DNS в Windows", "Open Windows DNS settings"), () =>
                {
                    try { Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }); }
                    catch (Exception ex) { Failed(ex); }
                });
                Cancel(page);
                page.Children.Add(report);
                break;
            case 4:
                Add(page, L("Сохранить журнал и результаты…", "Save log and results…"), ExportLog);
                page.Children.Add(journal);
                break;
        }
        Status();
    }

    private void Home()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Add(page, L("Открыть с правами администратора", "Reopen as administrator"), () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                        { UseShellExecute = true, Verb = "runas", Arguments = "--takeover" });
                    quit = true;
                    Close();
                }
                catch (Exception ex) { Failed(ex); }
            });
        var servicePanel = Card(page);
        Label(servicePanel, L("Сервисы", "Services"), 19);
        var services = new WrapPanel();
        servicePanel.Children.Add(services);
        for (int index = 0; index < Services.Length; index++)
        {
            int selected = index;
            Check(services, index == 2 ? L("Нейросети", "AI services") : Services[index], prefs.Enabled[index],
                value => { prefs.Enabled[selected] = value; RememberedProfile(); Persist(); });
        }
        Check(services, L("Все сайты", "All sites"), all, value =>
        {
            all = value && MessageBox.Show(this,
                L("Обрабатывать TLS по TCP 443 на всех сайтах?", "Apply to TLS over TCP 443 on all sites?"),
                Title, MessageBoxButton.YesNo) == MessageBoxResult.Yes;
            if (value != all) Draw();
            prefs.AllSites = all;
            Persist();
            RememberedProfile();
        });
        var controls = Card(page);
        badge.Foreground = Accent;
        controls.Children.Add(badge);
        PopulateProfiles();
        controls.Children.Add(profiles);
        var row = new WrapPanel();
        controls.Children.Add(row);
        toggleButton = Action(row, "", async ct =>
        {
            if (engine.Running) { engine.Stop(); Log(L("Обход остановлен", "Bypass stopped")); return; }
            string[] domains = Targets();
            await ActivateProfile(Selected(), domains, all, ct);
        }, true);
        Action(row, L("Подобрать стратегию", "Find a strategy"), ct => Scan(true, false, ct));
        Action(row, L("Сгенерировать стратегию", "Generate a strategy"), ct => Scan(true, true, ct));
        Action(row, L("Обновить профиль сети", "Refresh network profile"), RefreshProfile);
        Action(row, L("Проверить доступ к нейросетям", "Test AI service access"), TestChatGpt);
        var results = new WrapPanel();
        page.Children.Add(results);
        saveButton = Add(results, L("Сохранить в стратегии", "Save to strategies"), SaveWinner);
        Add(results, L("Сохранить журнал…", "Save log…"), ExportLog);
        Cancel(results);
        page.Children.Add(report);
    }

    private void Strategies()
    {
        PopulateProfiles();
        page.Children.Add(profiles);
        var name = new TextBox { Margin = new Thickness(0, 12, 0, 4), Padding = new Thickness(8) };
        Label(page, L("Имя копии выбранного профиля", "Name for a copy of the selected profile"), 14, Muted);
        page.Children.Add(name);
        Add(page, L("Сохранить профиль", "Save profile"), () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            var profile = Selected() with { Name = name.Text.Trim() };
            prefs.Saved.Add(profile);
            Persist();
            PopulateProfiles(profile);
        });
    }

    private void Hosts()
    {
        for (int index = 0; index < Services.Length; index++)
        {
            int selected = index;
            var panel = Card(page);
            Label(panel, index == 2 ? L("Нейросети", "AI services") : Services[index], 18);
            var editor = new TextBox { Text = prefs.Hosts[index], AcceptsReturn = true, MinHeight = 85, Padding = new Thickness(8) };
            panel.Children.Add(editor);
            Add(panel, L("Сохранить домены", "Save domains"), () =>
            {
                try { prefs.Hosts[selected] = string.Join('\n', ParseHosts(editor.Text)); Persist(); Log(L("Хостлист сохранён", "Host list saved")); }
                catch (ArgumentException ex) { Failed(ex); }
            });
        }
    }

    private async Task ActivateProfile(Profile profile, string[] domains, bool scope, CancellationToken token)
    {
        string network = NetworkKey();
        await engine.Start(profile, domains, scope, false, token);
        activeProfile = profile;
        activeNetwork = network;
        Log(profile.Name);
    }

    private async Task TestChatGpt(CancellationToken token)
    {
        const string domain = "chatgpt.com";
        string[] aiDomains = [domain, "claude.ai", "gemini.google.com", "grok.com"];
        var profile = engine.Running ? activeProfile ?? Selected() : Selected();
        bool temporary = !engine.Running;
        Append($"\n{DateTime.Now:HH:mm:ss}  {profile.Name} / {domain}\n");
        try
        {
            if (temporary)
            {
                await engine.Start(profile, aiDomains, false, false, token);
            }
            foreach (var (protocol, version) in new[] { (SslProtocols.Tls12, "1.2"), (SslProtocols.Tls13, "1.3") })
                Append($"{domain,-32}  TLS {version}  {await Probe(domain, protocol, token)}\n");
            var target = new Uri("https://chatgpt.com/");
            await ReportWebAccess(false, token);
            if (!HttpClient.DefaultProxy.IsBypassed(target)) await ReportWebAccess(true, token);
        }
        finally { if (temporary) engine.Stop(); }
    }

    private async Task ReportWebAccess(bool useProxy, CancellationToken token, string[]? selectedDomains = null)
    {
        using var handler = new HttpClientHandler { UseProxy = useProxy, UseCookies = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        Append(useProxy
            ? L("HTTPS через системный прокси, без авторизации\n", "HTTPS via system proxy, unauthenticated\n")
            : L("HTTPS без системного прокси и авторизации\n", "HTTPS without system proxy or authentication\n"));
        foreach (string url in new[] { "https://chatgpt.com/", "https://chatgpt.com/backend-api/codex/responses",
            "https://claude.ai/", "https://gemini.google.com/", "https://grok.com/" })
        {
            var uri = new Uri(url);
            if (selectedDomains is not null && !selectedDomains.Contains(uri.Host)) continue;
            string path = uri.Host + uri.AbsolutePath;
                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    bool challenge = response.Headers.TryGetValues("cf-mitigated", out var mitigations) &&
                        mitigations.Any(value => value.Equals("challenge", StringComparison.OrdinalIgnoreCase));
                    string ray = response.Headers.TryGetValues("cf-ray", out var rays) ? rays.FirstOrDefault() ?? "" : "";
                    Append($"{path}  HTTP {(int)response.StatusCode} {response.StatusCode}" +
                        (challenge ? "  Cloudflare challenge" : "") + (ray.Length > 0 ? $"  CF-Ray {ray}" : "") + "\n");
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                        Append(L("HTTPS-соединение установлено. Сервис отклонил запрос.\n", "HTTPS connected. The service denied the request.\n"));
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.MethodNotAllowed)
                        Append(L("Узел ответил. Доступ с учётной записью проверяется в Codex.\n",
                            "The endpoint responded. Account access is checked in Codex.\n"));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { Append($"{path}  HTTPS timeout\n"); }
                catch (HttpRequestException ex) { Append($"{path}  HTTPS: {ex.HttpRequestError}\n"); }
        }
    }

    private void ExportLog()
    {
        var dialog = new SaveFileDialog {
            FileName = $"LocalDpi-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt",
            Filter = "Text (*.txt)|*.txt",
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, journal.Text + "\n--- TLS ---\n" + report.Text); }
        catch (IOException ex) { Failed(ex); }
        catch (UnauthorizedAccessException ex) { Failed(ex); }
    }

    private async Task RefreshProfile(CancellationToken token)
    {
        string[] domains = Targets();
        if (domains.Length == 0) throw new ArgumentException(L("Выберите сервисы для проверки.", "Select services to test."));
        string network = NetworkKey();
        string scope = Scope();
        Profile? previous = engine.Running ? activeProfile : null;
        bool changed = engine.Running && network != activeNetwork;
        activeNetwork = network;
        Log(L("Проверка профиля сети", "Checking network profile"));
        try
        {
            if (previous is not null && !changed)
            {
                using var gate = new SemaphoreSlim(6);
                bool[] results = await Task.WhenAll(ProbeTargets(domains).Select(async domain =>
                {
                    await gate.WaitAsync(token);
                    try
                    {
                        return await Probe(domain, SslProtocols.Tls12, token) == "OK" ||
                            await Probe(domain, SslProtocols.Tls13, token) == "OK";
                    }
                    finally { gate.Release(); }
                }));
                if (results.All(result => result) && engine.Running && network == NetworkKey())
                {
                    Log(L("Активный профиль прошёл проверку", "Active profile passed"));
                    return;
                }
            }

            await Scan(true, false, token);
            if (winner is null) await Scan(true, true, token);
            if (winner is { } updated && winningNetwork == network && network == NetworkKey() && Scope() == scope)
            {
                if (previous is not null) await ActivateProfile(updated, domains, all, token);
                SaveWinner();
                return;
            }
            Log(L("Рабочий профиль не найден", "No working profile found"));
        }
        finally
        {
            if (previous is not null && !engine.Running && !quit)
            {
                await ActivateProfile(previous, domains, all, CancellationToken.None);
                activeNetwork = network;
                Log(L("Восстановлен предыдущий профиль", "Previous profile restored"));
            }
        }
    }

    private async Task Scan(bool strategies, bool generated, CancellationToken token)
    {
        string[] domains = Targets();
        if (domains.Length == 0) throw new ArgumentException(L("Выберите сервисы для проверки.", "Select services to test."));
        bool scope = all;
        string scopeKey = Scope();
        string network = NetworkKey();
        winner = null;
        report.Clear();
        prefs.Networks.TryGetValue(network + "|" + scopeKey, out var remembered);
        var candidates = (generated
            ? Presets.Concat(new[] { "multisplit", "multidisorder" }.SelectMany(method => new[] { "1", "3", "5" }
                .Select(pos => new Profile($"{method} / {pos}", method, pos)))).ToArray()
            : (remembered is null ? Presets : new[] { remembered }.Concat(Presets))
                .Concat(prefs.Saved).DistinctBy(p => JsonSerializer.Serialize(p with { Name = "" })).ToArray()).ToList();
        if (!strategies) candidates = [new Profile(L("Прямая проверка", "Direct check"), "", "")];
        else if (!engine.ComboInstalled) candidates.RemoveAll(profile => profile.Function == Engine.Combo);
        int initialCount = candidates.Count;
        string[] probeDomains = ProbeTargets(domains);
        if (probeDomains.Length != domains.Length)
            Append(L("googlevideo.com остаётся в хостлисте. Проверка видео выполняется по redirector.googlevideo.com.\n\n",
                "googlevideo.com remains in the host list. The video probe uses redirector.googlevideo.com.\n\n"));
        var probes = probeDomains.SelectMany(domain => new[] { SslProtocols.Tls12, SslProtocols.Tls13 }.Select(tls => (domain, tls))).ToArray();
        int best = 0;
        Profile? bestPartial = null;
        int bestPartialScore = 0;
        string[] aiTargets = probeDomains.Intersect(["chatgpt.com", "claude.ai", "gemini.google.com", "grok.com"]).ToArray();
        var domainChoices = new Dictionary<string, (Profile Profile, int Score)>();
        int fallbackScore = -1;
        Profile fallback = Presets[0];
        try
        {
            engine.Stop();
            for (int index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                token.ThrowIfCancellationRequested();
                if (NetworkKey() != network) throw new InvalidOperationException(L("Сеть изменилась.", "Network changed."));
                Append($"{candidate.Name}\n");
                if (strategies) await engine.Start(candidate, domains, scope, false, token);
                int success = 0;
                var reachable = new Dictionary<string, int>();
                using var gate = new SemaphoreSlim(6);
                await Task.WhenAll(probes.Select(async probe =>
                {
                    await gate.WaitAsync(token);
                    try
                    {
                        string outcome = await Probe(probe.domain, probe.tls, token);
                        if (outcome == "OK") { success++; reachable[probe.domain] = reachable.GetValueOrDefault(probe.domain) + 1; }
                        string version = probe.tls == SslProtocols.Tls12 ? "1.2" : "1.3";
                        Append($"{probe.domain,-32}  TLS {version}  {(outcome == "OK" ? L("рукопожатие прошло", "handshake passed") : outcome)}\n");
                    }
                    finally { gate.Release(); }
                }));
                if (strategies && !engine.Running)
                    throw new InvalidOperationException(L("Движок завершился во время проверки.", "Engine exited during tests."));
                Append($"TLS {success}/{probes.Length}\n\n");
                if (strategies && success > bestPartialScore)
                {
                    bestPartialScore = success;
                    bestPartial = candidate;
                }
                if (strategies && candidate.Rules is null && candidate.Function != Engine.Combo)
                {
                    if (success > fallbackScore) { fallback = candidate; fallbackScore = success; }
                    foreach (var (domain, score) in reachable)
                        if (!domainChoices.TryGetValue(domain, out var previous) || score > previous.Score)
                            domainChoices[domain] = (candidate, score);
                }
                if (strategies && reachable.Count == probeDomains.Length && success > best && NetworkKey() == network)
                {
                    best = success;
                    winner = candidate;
                    winningNetwork = network;
                    winningScope = scopeKey;
                    PopulateProfiles(candidate);
                    if (success == probes.Length) break;
                }
                if (strategies && index == initialCount - 1 && domainChoices.Count == probeDomains.Length)
                {
                    if (probeDomains.Length != domains.Length)
                        domainChoices["googlevideo.com"] = domainChoices["redirector.googlevideo.com"];
                    var groups = domainChoices.GroupBy(pair => (pair.Value.Profile.Function, pair.Value.Profile.Split)).ToArray();
                    if (groups.Length > 1)
                        candidates.Add(new Profile(L("Смешанная стратегия", "Mixed strategy"), fallback.Function, fallback.Split,
                            groups.Select(group => new ProfileRule(group.Key.Function, group.Key.Split, group.Select(pair => pair.Key).ToArray())).ToArray()));
                }
            }
            if (strategies && aiTargets.Length > 0 && (winner ?? bestPartial) is { } webProfile &&
                NetworkKey() == network)
            {
                Append(L("Проверка HTTP для профиля ", "HTTP check for profile ") + webProfile.Name + "\n");
                await engine.Start(webProfile, domains, scope, false, token);
                await ReportWebAccess(false, token, aiTargets);
            }
        }
        finally { engine.Stop(); }
        if (strategies)
            Append(winner is null
                ? L("Нет профиля, прошедшего TLS на всех доменах.\n", "No profile completed TLS for every domain.\n")
                : L("По результатам TLS выбран профиль: ", "Selected from TLS results: ") + winner.Name + "\n");
    }

    private static async Task<string> Probe(string domain, SslProtocols protocol, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        string phase = "DNS";
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(domain, timeout.Token);
            if (addresses.Length == 0) return "DNS: no address";
            phase = "TCP";
            using var socket = new TcpClient();
            await socket.ConnectAsync(addresses, 443, timeout.Token);
            phase = "TLS";
            using var ssl = new SslStream(socket.GetStream());
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = domain, EnabledSslProtocols = protocol
            }, timeout.Token);
            return "OK";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return phase + ": timeout"; }
        catch (SocketException ex) { return phase + ": " + ex.SocketErrorCode; }
        catch (AuthenticationException) { return "TLS: handshake or certificate error"; }
        catch (IOException) { return phase + ": connection error"; }
    }

    private static string[] ProbeTargets(string[] domains) =>
        domains.Contains("redirector.googlevideo.com")
            ? domains.Where(domain => domain != "googlevideo.com").ToArray()
            : domains;

    private void SaveWinner()
    {
        if (winner is null || winningNetwork != NetworkKey() || winningScope != Scope()) return;
        if (!prefs.Saved.Contains(winner)) prefs.Saved.Add(winner);
        prefs.Networks[winningNetwork + "|" + winningScope] = winner;
        Persist();
        PopulateProfiles(winner);
        Log(L("Профиль сохранён для сети", "Profile saved for this network"));
    }

    private void PopulateProfiles(Profile? selected = null)
    {
        selected ??= profiles.SelectedItem as Profile;
        if (selected is null) prefs.Networks.TryGetValue(NetworkKey() + "|" + Scope(), out selected);
        Profile[] items = Presets.Concat(prefs.Saved).Concat(selected is null ? [] : new[] { selected }).Distinct().ToArray();
        profiles.ItemsSource = items;
        profiles.SelectedItem = selected ?? items[0];
    }

    private void RememberedProfile()
    {
        if (prefs.Networks.TryGetValue(NetworkKey() + "|" + Scope(), out var profile))
            PopulateProfiles(profile);
    }

    private Profile Selected() => profiles.SelectedItem as Profile ?? Presets[0];
    private string Scope() => (all ? "all|" : "hosts|") + string.Join(',', Targets().Order());
    private string[] Targets() => prefs.Hosts.Where((_, index) => prefs.Enabled[index] || (all && !prefs.Enabled.Any(value => value)))
        .SelectMany(ParseHosts).Distinct().ToArray();

    private static string[] ParseHosts(string value)
    {
        return value.Split((char[]?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(host =>
            {
                string ascii = new IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant();
                if (!ascii.Contains('.') || Uri.CheckHostName(ascii) != UriHostNameType.Dns ||
                    ascii.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-')))
                    throw new ArgumentException(host);
                return ascii;
            }).Distinct().ToArray();
    }

    private static string NetworkKey()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses.Select(g => $"{n.Id}/{g.Address}"))
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', interfaces))));
    }

    private void Append(string text) { report.AppendText(text); report.ScrollToEnd(); }
    private void Log(string text) { journal.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\n"); journal.ScrollToEnd(); }
    private void Failed(Exception ex) { Log(ex.Message); MessageBox.Show(this, ex.Message, Title); }
    private void Status()
    {
        badge.Text = task is not null ? L("Выполняется операция", "Working") : engine.Running ? L("Обход включён", "Bypass active") : L("Обход остановлен", "Bypass stopped");
        if (toggleButton is not null)
            toggleButton.Content = engine.Running ? L("Остановить", "Stop") : L("Включить обход", "Start bypass");
        foreach (var button in actionButtons) button.IsEnabled = task is null;
        if (cancelButton is not null) cancelButton.IsEnabled = task is not null;
        if (saveButton is not null) saveButton.IsEnabled = task is null && winner is not null;
        navigation.IsEnabled = task is null;
        foreach (var panel in page.Children.OfType<Border>())
            if (panel.Child is StackPanel card)
                foreach (var service in card.Children.OfType<WrapPanel>().SelectMany(row => row.Children.OfType<CheckBox>()))
                    service.IsEnabled = task is null && !engine.Running;
        profiles.IsEnabled = task is null && !engine.Running;
    }

    private Button Action(Panel parent, string label, Func<CancellationToken, Task> action, bool primary = false)
    {
        var button = Button(parent, label, primary);
        actionButtons.Add(button);
        button.Click += async (_, _) =>
            await RunOperation(action);
        return button;
    }

    private async Task RunOperation(Func<CancellationToken, Task> action, bool background = false)
    {
        if (task is not null) return;
        using var source = new CancellationTokenSource();
        task = source;
        Status();
        try { await action(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { Log(L("Операция отменена", "Cancelled")); }
        catch (Exception ex)
        {
            if (!quit && !background) Failed(ex);
            else if (!quit) Log(ex.Message);
        }
        finally { task = null; if (!quit) Status(); }
    }

    private void Cancel(Panel parent) => cancelButton = Add(parent, L("Отменить операцию", "Cancel operation"), () => task?.Cancel());

    private static StackPanel Card(Panel parent)
    {
        var child = new StackPanel();
        parent.Children.Add(new Border { Child = child, Background = Panel, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) });
        return child;
    }

    private static void Label(Panel parent, string text, double size, Brush? color = null)
    {
        parent.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size,
            Foreground = color ?? Ink, Margin = new Thickness(0, 3, 0, 8) });
    }

    private static void Check(Panel parent, string label, bool initial, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, Foreground = Ink, IsChecked = initial, Margin = new Thickness(0, 4, 20, 4) };
        box.Click += (_, _) => changed(box.IsChecked == true);
        parent.Children.Add(box);
    }

    private static Button Add(Panel parent, string label, Action click, bool primary = false)
    {
        var button = Button(parent, label, primary);
        button.Click += (_, _) => click();
        return button;
    }

    private static Button Button(Panel parent, string label, bool primary)
    {
        var button = new Button { Content = label, Padding = new Thickness(13, 7, 13, 7),
            Margin = new Thickness(0, 4, 7, 4), Background = primary ? Accent : BrushOf("#393939"),
            Foreground = primary ? BrushOf("#171717") : Ink, BorderBrush = BrushOf("#646464") };
        parent.Children.Add(button);
        return button;
    }
}
