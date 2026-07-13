using System.IO;

namespace ArkFixYourQueues;

internal static class DiagnosticLog
{
    private static readonly string FileName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArkFixYourQueues", "diagnostic.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
            File.AppendAllText(FileName, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
