using System.Text.RegularExpressions;

namespace ArkFixYourQueues;

public sealed record AsaLocalServer(string Name, string SettingsPath);
public sealed record AsaFavoriteServer(string Name, string Map, bool Official)
{
    public override string ToString() => $"★ {Name} — {Map} — {(Official ? "Official" : "Unofficial")}";
}

public static partial class AsaLocalDiscovery
{
    public static AsaLocalServer? FindLastJoined(IEnumerable<string> settingsPaths)
    {
        foreach (var path in settingsPaths.Where(File.Exists))
        {
            try
            {
                var line = File.ReadLines(path).LastOrDefault(value =>
                    value.StartsWith("LastJoinedSessionPerCategory=", StringComparison.OrdinalIgnoreCase));
                if (line is null) continue;
                var value = line[(line.IndexOf('=') + 1)..].Trim();
                var name = VersionSuffix().Replace(value, string.Empty).Trim();
                if (name.Length > 0) return new AsaLocalServer(name, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static IReadOnlyList<AsaFavoriteServer> FindFavorites(IEnumerable<string> profilePaths)
    {
        var results = new List<AsaFavoriteServer>();
        foreach (var path in profilePaths.Where(File.Exists))
        {
            try
            {
                var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
                foreach (Match match in FavoriteEntry().Matches(text))
                {
                    var rawName = match.Groups["name"].Value.Trim();
                    var name = VersionSuffix().Replace(rawName, string.Empty).Trim();
                    if (name.Length == 0 || results.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    results.Add(new AsaFavoriteServer(name, match.Groups["map"].Value, match.Groups["official"].Value == "1"));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return results;
    }

    [GeneratedRegex(@"\s+-\s+\(v[^)]*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"(?<name>[A-Za-z0-9][^\0\r\n]{2,120}?\s+-\s+\(v[^)]*\))\s+Map=(?<map>[A-Za-z0-9_]+)[^\0\r\n]{0,160}?Official=(?<official>[01])", RegexOptions.IgnoreCase)]
    private static partial Regex FavoriteEntry();
}
