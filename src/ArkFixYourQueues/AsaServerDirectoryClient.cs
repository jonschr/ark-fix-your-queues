using System.Net.Http;
using System.Text.Json;

namespace ArkFixYourQueues;

public sealed class AsaServerDirectoryClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string OfficialUrl = "https://cdn2.arkdedicated.com/servers/asa/officialserverlist.json";
    private const string UnofficialUrl = "https://cdn2.arkdedicated.com/servers/asa/unofficialserverlist.json";

    public async Task<ServerTarget> ResolveAsync(string name, bool official, CancellationToken cancellationToken)
    {
        using var stream = await Http.GetStreamAsync(official ? OfficialUrl : UnofficialUrl, cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("Name", out var nameValue) ||
                !string.Equals(nameValue.GetString(), name, StringComparison.OrdinalIgnoreCase)) continue;
            var ip = item.GetProperty("IP").GetString();
            var port = item.GetProperty("Port").GetInt32();
            if (string.IsNullOrWhiteSpace(ip) || port is < 1 or > 65535)
                throw new InvalidOperationException("ASA returned an invalid game address for this server.");
            return new ServerTarget(name, ip, port, null);
        }
        throw new InvalidOperationException($"{name} was not found in ASA's current {(official ? "official" : "unofficial")} server directory.");
    }
}
