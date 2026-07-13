using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace ArkFixYourQueues;

public sealed record ArkStatusSearchResult(long Id, string Name, string Map, string Status, int Players, int MaxPlayers)
{
    public override string ToString() => $"{Name} — {Players}/{MaxPlayers} — {Map} — {Status}";
}

public sealed class ArkStatusClient
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://arkstatus.com/api/v1/") };

    public async Task<IReadOnlyList<ArkStatusSearchResult>> SearchAsync(string apiKey, string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"servers?search={Uri.EscapeDataString(query)}&status=all&server_type=all&per_page=25");
        request.Headers.Add("X-API-Key", apiKey.Trim());
        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(item => new ArkStatusSearchResult(
            item.GetProperty("id").GetInt64(),
            item.GetProperty("name").GetString() ?? "Unknown server",
            ReadString(item, "map", "Unknown map"),
            ReadString(item, "status", "unknown"),
            ReadInt(item, "players"),
            ReadInt(item, "max_players"))).ToArray();
    }

    public async Task<ServerTarget> ResolveAsync(string apiKey, ArkStatusSearchResult result, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"servers/{result.Id}");
        request.Headers.Add("X-API-Key", apiKey.Trim());
        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var data = json.RootElement.GetProperty("data");
        var connection = data.GetProperty("connection_info");
        var ip = connection.GetProperty("ip").GetString();
        var port = connection.GetProperty("port").GetInt32();
        if (string.IsNullOrWhiteSpace(ip) || port is < 1 or > 65535)
            throw new InvalidOperationException("The provider did not return usable connection information.");
        return new ServerTarget(result.Name, ip, port, result.Id);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"ARK Status returned {(int)response.StatusCode}: {detail}");
    }

    private static string ReadString(JsonElement item, string name, string fallback) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static int ReadInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
