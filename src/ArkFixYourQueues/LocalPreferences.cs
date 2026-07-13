using System.IO;
using System.Text.Json;

namespace ArkFixYourQueues;

public sealed record LocalPreferences(string? ServerName, string? Endpoint, int RetrySeconds)
{
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArkFixYourQueues", "settings.json");

    public static LocalPreferences Load()
    {
        try
        {
            return File.Exists(PathName)
                ? JsonSerializer.Deserialize<LocalPreferences>(File.ReadAllText(PathName)) ?? new(null, null, 10)
                : new(null, null, 10);
        }
        catch { return new(null, null, 10); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(this));
        }
        catch { /* Preferences are a convenience; failure must not affect joining safety. */ }
    }
}
