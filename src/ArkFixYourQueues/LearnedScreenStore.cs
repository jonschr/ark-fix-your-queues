using System.Text.Json;
using System.IO;

namespace ArkFixYourQueues;

internal sealed class LearnedScreen
{
    public string Name { get; set; } = "Learned action";
    public string Action { get; set; } = "Click target";
    public double X { get; set; }
    public double Y { get; set; }
    public byte[] Signature { get; set; } = [];
    public DateTimeOffset LearnedAt { get; set; } = DateTimeOffset.Now;
}

internal sealed class LearnedScreenStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArkJoinAssist", "learned-screens.json");

    public List<LearnedScreen> Screens { get; set; } = [];

    public static LearnedScreenStore Load()
    {
        try
        {
            return JsonSerializer.Deserialize<LearnedScreenStore>(File.ReadAllText(FilePath)) ?? new LearnedScreenStore();
        }
        catch { return new LearnedScreenStore(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
