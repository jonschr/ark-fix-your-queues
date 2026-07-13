using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArkFixYourQueues;

internal sealed record PendingUpdate(string Version, string ZipPath);

internal static class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/jonschr/ark-fix-your-queues/releases/latest";
    private const string ZipAssetName = "ARK-Join-Assist-win-x64.zip";
    private const string HashAssetName = "ARK-Join-Assist-win-x64.zip.sha256";

    public static async Task<PendingUpdate?> CheckAndDownloadAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ARK-Join-Assist-Updater");
        using var response = await client.GetAsync(LatestReleaseApi, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tag = release.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var available)) return null;
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        if (available <= current) return null;

        string? zipUrl = null, hashUrl = null;
        foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == ZipAssetName) zipUrl = url;
            if (name == HashAssetName) hashUrl = url;
        }
        if (zipUrl is null || hashUrl is null) return null;

        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArkFixYourQueues", "updates", available.ToString());
        Directory.CreateDirectory(updateDirectory);
        var zipPath = Path.Combine(updateDirectory, ZipAssetName);
        var expectedHash = (await client.GetStringAsync(hashUrl, cancellationToken))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        var bytes = await client.GetByteArrayAsync(zipUrl, cancellationToken);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded update did not match its published SHA-256 checksum.");
        await File.WriteAllBytesAsync(zipPath, bytes, cancellationToken);
        using (ZipFile.OpenRead(zipPath)) { }
        return new PendingUpdate(available.ToString(), zipPath);
    }

    public static bool TryLaunchOnExit(PendingUpdate update)
    {
        try
        {
            var installedUpdater = Path.Combine(AppContext.BaseDirectory, "ArkJoinAssist.Updater.exe");
            if (!File.Exists(installedUpdater)) return false;
            var temporaryUpdater = Path.Combine(Path.GetTempPath(), $"ArkJoinAssist.Updater-{Guid.NewGuid():N}.exe");
            File.Copy(installedUpdater, temporaryUpdater, true);
            Process.Start(new ProcessStartInfo
            {
                FileName = temporaryUpdater,
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"--pid {Environment.ProcessId} --zip \"{update.ZipPath}\" --install \"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\" --app \"ArkFixYourQueues.exe\""
            });
            return true;
        }
        catch { return false; }
    }
}
