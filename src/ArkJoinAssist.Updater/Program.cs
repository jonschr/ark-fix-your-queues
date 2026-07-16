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
    WaitForAppProcesses(appName, TimeSpan.FromSeconds(60));
    var staging = Path.Combine(Path.GetTempPath(), $"ArkJoinAssist-Update-{Guid.NewGuid():N}");
    Directory.CreateDirectory(staging);
    ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
    var backup = installPath.TrimEnd(Path.DirectorySeparatorChar) + ".previous";
    if (Directory.Exists(backup)) DeleteDirectoryWithRetry(backup, TimeSpan.FromSeconds(30));
    MoveDirectoryWithRetry(installPath, backup, TimeSpan.FromSeconds(30));
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
    try { File.Delete(Path.Combine(Path.GetTempPath(), "ArkJoinAssist-update-error.txt")); } catch { }
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

static void WaitForAppProcesses(string appName, TimeSpan timeout)
{
    var processName = Path.GetFileNameWithoutExtension(appName);
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        var running = Process.GetProcessesByName(processName)
            .Where(process => process.Id != Environment.ProcessId)
            .ToArray();
        if (running.Length == 0) return;
        if (DateTime.UtcNow >= deadline)
            throw new IOException($"Timed out waiting for {running.Length} older app process(es) to exit.");
        foreach (var process in running)
        {
            try { process.WaitForExit(500); }
            catch { }
            finally { process.Dispose(); }
        }
    }
}

static void MoveDirectoryWithRetry(string source, string destination, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException) when (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
        }
    }
}

static void DeleteDirectoryWithRetry(string path, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (IOException) when (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
        }
    }
}
