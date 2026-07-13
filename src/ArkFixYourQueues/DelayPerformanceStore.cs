using System.IO;
using System.Text.Json;

namespace ArkFixYourQueues;

internal sealed record DelayPerformance(int Attempts, int LoadingGlobes);

internal sealed class DelayPerformanceStore
{
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArkFixYourQueues", "delay-performance.json");

    private readonly Dictionary<int, DelayPerformance> _values;

    private DelayPerformanceStore(Dictionary<int, DelayPerformance>? values) =>
        _values = values ?? new Dictionary<int, DelayPerformance>();

    public static DelayPerformanceStore Load()
    {
        try
        {
            var values = File.Exists(PathName)
                ? JsonSerializer.Deserialize<Dictionary<int, DelayPerformance>>(File.ReadAllText(PathName))
                : null;
            return new DelayPerformanceStore(values);
        }
        catch { return new DelayPerformanceStore(null); }
    }

    public IReadOnlyDictionary<int, DelayPerformance> Values => _values;

    public DelayPerformance RecordAttempt(int spacingSeconds) => Update(spacingSeconds, attempts: 1, globes: 0);

    public DelayPerformance RecordGlobe(int spacingSeconds) => Update(spacingSeconds, attempts: 0, globes: 1);

    private DelayPerformance Update(int spacingSeconds, int attempts, int globes)
    {
        var current = _values.GetValueOrDefault(spacingSeconds, new DelayPerformance(0, 0));
        var updated = new DelayPerformance(current.Attempts + attempts, current.LoadingGlobes + globes);
        _values[spacingSeconds] = updated;
        Save();
        return updated;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(_values));
        }
        catch { /* Performance history must never interrupt the join workflow. */ }
    }
}
