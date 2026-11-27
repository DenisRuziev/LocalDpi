using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LocalDpi;

internal sealed record ProfileRule(string Function, string Split, string[] Domains);

internal sealed record Profile(string Name, string Function, string Split, ProfileRule[]? Rules = null)
{
    public override string ToString() => Name;
}

internal sealed class Engine(string directory) : IDisposable
{
    internal const string Combo = "combo";
    private const string DiscordVoicePorts = "19294-19344,50000-65535";
    private const string Release = "https://github.com/bol-van/zapret2/releases/download/v1.0.7/zapret2-v1.0.7.zip";
    private const string Digest = "35E14E4E1F345B870C3B87269FA45353EFD597241A25304D38375C46B9FD2967";
    private static readonly Dictionary<string, string> Manifest = ReadManifest();
    private Process? process;
    private SafeFileHandle? job;
    public bool Running => process is { HasExited: false };
    public bool Installed => Manifest.Keys.Where(IsCoreFile).All(path => File.Exists(FilePath(path)));
    public bool ComboInstalled => Manifest.Keys.All(path => File.Exists(FilePath(path)));

    private static bool IsCoreFile(string path) => path.StartsWith("binaries/", StringComparison.Ordinal) ||
        path.StartsWith("lua/", StringComparison.Ordinal);

    private static Dictionary<string, string> ReadManifest()
    {
        using var reader = new StreamReader(typeof(Engine).Assembly.GetManifestResourceStream("LocalDpi.engine.sha256")!);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2)).ToDictionary(parts => parts[1], parts => parts[0]);
    }

    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    private string FilePath(string path) => Path.Combine(directory, Path.GetFileName(path));

    public async Task Install(CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
        using var response = await http.GetAsync(Release, token);
        response.EnsureSuccessStatusCode();
        using var data = new MemoryStream(await response.Content.ReadAsByteArrayAsync(token));
        if (Hash(data) != Digest) throw new InvalidDataException("ZIP SHA-256 mismatch");
        data.Position = 0;
        using var zip = new ZipArchive(data);
        var verified = new Dictionary<string, byte[]>();
        foreach (var (path, hash) in Manifest)
        {
            using var entry = zip.GetEntry("zapret2-v1.0.7/" + path)?.Open()
                ?? throw new InvalidDataException(path);
            using var buffer = new MemoryStream();
            await entry.CopyToAsync(buffer, token);
            buffer.Position = 0;
            if (Hash(buffer) != hash) throw new InvalidDataException("SHA-256 mismatch: " + path);
            verified.Add(FilePath(path), buffer.ToArray());
        }
        foreach (var (path, bytes) in verified)
        {
            if (File.Exists(path) && (await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(bytes))
                continue;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
    }

    public async Task Start(Profile profile, string[] domains, bool all, bool dryRun, CancellationToken token)
    {
        Stop();
        if (!Environment.Is64BitProcess || !OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Windows 10/11 x64");
        if (!all && domains.Length == 0) throw new ArgumentException("Select at least one service");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory)
            .Any(path => !Manifest.Keys.Any(item => string.Equals(FilePath(item), path, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Unexpected file in engine directory");
        var files = new List<FileStream>();
        try
        {
            foreach (var (path, hash) in Manifest.Where(item => profile.Function == Combo || IsCoreFile(item.Key)))
            {
                string local = FilePath(path);
                if (!File.Exists(local)) throw new FileNotFoundException("Download verified engine in Settings", local);
                var file = File.Open(local, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Add(file);
                if (Hash(file) != hash) throw new InvalidDataException("SHA-256 mismatch: " + path);
            }

            var info = new ProcessStartInfo(FilePath("winws2.exe")) {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (string argument in BuildArguments(profile, domains, all))
                info.ArgumentList.Add(argument);
            if (dryRun) info.ArgumentList.Add("--dry-run");
            job = ProcessJob.Create();
            process = Process.Start(info) ?? throw new InvalidOperationException("winws2");
            ProcessJob.Attach(job, process);
            var output = process.StandardOutput.ReadToEndAsync(token);
            var errors = process.StandardError.ReadToEndAsync(token);
            if (dryRun)
            {
                await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
                if (process.ExitCode != 0) throw new InvalidOperationException((await errors) + (await output));
                Stop();
                return;
            }
            await Task.Delay(650, token);
            if (process.HasExited) throw new InvalidOperationException($"winws2 ({process.ExitCode})\n{await errors}{await output}");
        }
        catch { Stop(); throw; }
        finally { foreach (var file in files) file.Dispose(); }
    }

    internal static IEnumerable<string> BuildArguments(Profile profile, string[] domains, bool all)
    {
        if (profile.Function == Combo)
        {
            foreach (string argument in ComboArguments(domains, all)) yield return argument;
            yield break;
        }
        yield return "--wf-tcp-out=443";
        yield return "--lua-init=@zapret-lib.lua";
        yield return "--lua-init=@zapret-antidpi.lua";
        var remaining = domains.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rules = (profile.Rules ?? []).SelectMany(rule => rule.Domains.Select(domain => (rule, domain)))
            .OrderByDescending(item => item.domain.Length);
        bool hasRule = false;
        foreach (var (rule, domain) in rules)
        {
            if (!remaining.Remove(domain)) continue;
            if (hasRule) yield return "--new";
            foreach (string argument in Rule(rule.Function, rule.Split, [domain])) yield return argument;
            hasRule = true;
        }
        if (all || remaining.Count > 0)
        {
            if (hasRule) yield return "--new";
            foreach (string argument in Rule(profile.Function, profile.Split, all ? [] : remaining.Order().ToArray()))
                yield return argument;
        }
    }

    private static IEnumerable<string> ComboArguments(string[] domains, bool all)
    {
        string[] discordHosts = domains.Where(domain => new[] { "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media" }
            .Any(suffix => domain == suffix || domain.EndsWith("." + suffix, StringComparison.Ordinal))).ToArray();
        bool discord = discordHosts.Length > 0;
        yield return discord ? "--wf-tcp-out=443-65535" : "--wf-tcp-out=443";
        yield return discord ? "--wf-udp-out=443," + DiscordVoicePorts : "--wf-udp-out=443";
        yield return "--lua-init=@zapret-lib.lua";
        yield return "--lua-init=@zapret-antidpi.lua";
        yield return "--blob=local_tls:@tls_clienthello_www_google_com.bin";
        yield return "--blob=local_quic:@quic_initial_www_google_com.bin";

        yield return "--filter-udp=443";
        yield return "--filter-l7=quic";
        if (!all) yield return HostFilter(domains);
        yield return "--payload=quic_initial";
        yield return "--lua-desync=fake:blob=local_quic:repeats=6";

        if (discord)
        {
            yield return "--new";
            yield return "--filter-udp=" + DiscordVoicePorts;
            yield return "--filter-l7=discord,stun";
            yield return "--lua-desync=fake:blob=local_quic:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=2";
        }

        string[] video = domains.Where(domain => domain is "youtu.be" or "youtubei.googleapis.com" or "yt3.ggpht.com" || new[] { "youtube.com", "googlevideo.com", "ytimg.com" }
            .Any(suffix => domain == suffix || domain.EndsWith("." + suffix, StringComparison.Ordinal))).ToArray();
        if (video.Length > 0)
        {
            yield return "--new";
            yield return "--filter-tcp=443";
            yield return "--filter-l7=tls";
            yield return HostFilter(video);
            yield return "--payload=tls_client_hello";
            yield return "--lua-desync=fake:blob=local_tls:tcp_md5:tcp_seq=-10000:repeats=6";
            yield return "--lua-desync=multidisorder:pos=1,midsld";
        }

        if (discord)
        {
            yield return "--new";
            yield return "--filter-tcp=443-65535";
            yield return "--filter-l7=tls";
            yield return HostFilter(discordHosts.Append("discord.media").Distinct());
            yield return "--payload=tls_client_hello";
            yield return "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4";
        }

        string[] remaining = domains.Except(video).Except(discordHosts).ToArray();
        if (all || remaining.Length > 0)
        {
            yield return "--new";
            yield return "--filter-tcp=443";
            yield return "--filter-l7=tls";
            if (!all) yield return HostFilter(remaining);
            yield return "--payload=tls_client_hello";
            yield return "--lua-desync=multisplit:pos=1,midsld";
        }
    }

    private static IEnumerable<string> Rule(string method, string split, string[] domains)
    {
        if (method is not ("multisplit" or "multidisorder" or "fakedsplit")
            || split is not ("1" or "2" or "3" or "5" or "midsld" or "1,midsld"))
            throw new ArgumentException("Invalid strategy");
        yield return "--filter-tcp=443";
        yield return "--filter-l7=tls";
        if (domains.Length > 0)
        {
            yield return HostFilter(domains);
        }
        yield return "--payload=tls_client_hello";
        yield return "--lua-desync=" + method + ":pos=" + split;
    }

    private static string HostFilter(IEnumerable<string> domains)
    {
        string[] entries = domains.ToArray();
        if (entries.Any(domain => Uri.CheckHostName(domain) != UriHostNameType.Dns ||
            domain.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-'))))
            throw new ArgumentException("Invalid strategy domain");
        return "--hostlist-domains=" + string.Join(',', entries);
    }

    public void Stop()
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(3000)) throw new TimeoutException("winws2");
            }
        }
        finally
        {
            job?.Dispose();
            job = null;
            process?.Dispose();
            process = null;
        }
    }

    public void Dispose() => Stop();
}
