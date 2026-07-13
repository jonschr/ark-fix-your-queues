using System.Net;

namespace ArkFixYourQueues;

public sealed record ServerTarget(string DisplayName, string Address, int Port, long? ProviderId = null)
{
    public string Endpoint => $"{Address}:{Port}";
    public string JoinCommand => $"open {Endpoint}";

    public static bool TryParse(string input, string? displayName, out ServerTarget? target, out string error)
    {
        target = null;
        error = string.Empty;
        var value = input.Trim();
        if (value.StartsWith("open ", StringComparison.OrdinalIgnoreCase)) value = value[5..].Trim();

        var colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value[(colon + 1)..], out var port) || port is < 1 or > 65535)
        {
            error = "Use an IP and game port, for example 1.2.3.4:7777.";
            return false;
        }

        var address = value[..colon].Trim();
        if (!IPAddress.TryParse(address, out _))
        {
            error = "ASA's open command requires an IP address, not a hostname.";
            return false;
        }

        target = new ServerTarget(string.IsNullOrWhiteSpace(displayName) ? value : displayName.Trim(), address, port);
        return true;
    }
}
