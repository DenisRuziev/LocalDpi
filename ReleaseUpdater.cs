using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LocalDpi;

internal static class ReleaseUpdater
{
    private const string InstallerName = "LocalDpi-Setup.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8911893D-C4EF-4FB4-B86D-BC72983F430E}_is1";
    private const int MaximumSize = 128 * 1024 * 1024;

    internal static string? Repository { get; } = typeof(ReleaseUpdater).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "UpdateRepository")?.Value;

    internal static string? InstalledDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (key?.GetValue("InstallLocation") is not string location || string.IsNullOrWhiteSpace(location))
            return null;
        string exe = Path.GetFullPath(Path.Combine(location, "LocalDpi.exe"));
        return string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(exe) : null;
    }

    internal static async Task<PendingUpdate?> DownloadAsync(HttpClient http, string repository, Version current,
        string destination, CancellationToken token)
    {
        if (!Regex.IsMatch(repository, @"\A[\w.-]+/[\w.-]+\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid GitHub repository");
        var release = await http.GetFromJsonAsync<Release>(
            $"https://api.github.com/repos/{repository}/releases/latest", token);
        if (release is null || release.Draft || release.Prerelease ||
            !Regex.IsMatch(release.Tag ?? "", @"\Av\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant) ||
            !Version.TryParse(release.Tag![1..], out var version) || version <= current)
            return null;

        var asset = release.Assets?.SingleOrDefault(item => item.Name == InstallerName);
        string url = $"https://github.com/{repository}/releases/download/{release.Tag}/{InstallerName}";
        if (asset is null || asset.Url != url || asset.Size <= 0 || asset.Size > MaximumSize ||
            !Regex.IsMatch(asset.Digest ?? "", @"\Asha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("GitHub release is missing a verifiable installer");

        byte[] bytes = await http.GetByteArrayAsync(url, token);
        if (bytes.Length != asset.Size || !Convert.ToHexString(SHA256.HashData(bytes))
            .Equals(asset.Digest![7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update installer SHA-256 mismatch");

        string staging = Path.Combine(destination, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string installer = Path.Combine(staging, InstallerName);
        try
        {
            await File.WriteAllBytesAsync(installer, bytes, token);
            return new PendingUpdate(version, installer);
        }
        catch
        {
            File.Delete(installer);
            Directory.Delete(staging);
            throw;
        }
    }

    internal static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(50), MaxResponseContentBufferSize = MaximumSize };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalDpi");
        return http;
    }

    internal static void CleanStaging(string destination)
    {
        if (!Directory.Exists(destination)) return;
        foreach (string folder in Directory.EnumerateDirectories(destination))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
            try
            {
                File.Delete(Path.Combine(folder, InstallerName));
                Directory.Delete(folder);
            }
            catch (IOException) { } // The installer can still be running after launching the new app.
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] Asset[]? Assets);

    private sealed record Asset([property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("size")] int Size,
        [property: JsonPropertyName("digest")] string? Digest);
}

internal sealed record PendingUpdate(Version Version, string Installer) : IDisposable
{
    internal void Launch(string installDirectory, string updateLog)
    {
        var info = new ProcessStartInfo(Installer) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
            "/LOCALDPIUPDATE", "/DIR=" + installDirectory, "/LOG=" + updateLog })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch update");
    }

    public void Dispose()
    {
        File.Delete(Installer);
        Directory.Delete(Path.GetDirectoryName(Installer)!);
    }
}
