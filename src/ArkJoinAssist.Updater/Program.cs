using System.Diagnostics;
using System.IO.Compression;

var values = args
    .Select((value, index) => (value, index))
    .Where(item => item.value.StartsWith("--") && item.index + 1 < args.Length)
    .ToDictionary(item => item.value, item => args[item.index + 1], StringComparer.OrdinalIgnoreCase);

if (!values.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out var pid) ||
    !values.TryGetValue("--zip", out var zipPath) ||
    !values.TryGetValue("--install", out var installPath) ||
    !values.TryGetValue("--app", out var appName)) return 2;

try
{
    try { Process.GetProcessById(pid).WaitForExit(30000); } catch { }
    var staging = Path.Combine(Path.GetTempPath(), $"ArkJoinAssist-Update-{Guid.NewGuid():N}");
    Directory.CreateDirectory(staging);
    ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
    var backup = installPath.TrimEnd(Path.DirectorySeparatorChar) + ".previous";
    if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
    Directory.Move(installPath, backup);
    try
    {
        Directory.Move(staging, installPath);
    }
    catch
    {
        if (!Directory.Exists(installPath) && Directory.Exists(backup))
            Directory.Move(backup, installPath);
        throw;
    }

    File.Delete(zipPath);
    Process.Start(new ProcessStartInfo
    {
        FileName = Path.Combine(installPath, appName),
        WorkingDirectory = installPath,
        UseShellExecute = true
    });
    try { Directory.Delete(backup, recursive: true); } catch { }
    return 0;
}
catch (Exception error)
{
    try
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ArkJoinAssist-update-error.txt"), error.ToString());
    }
    catch { }
    return 1;
}
