using System.Net;
using System.Net.Sockets;

namespace Wireflex.Core.Connection;

/// <summary>Lê a saída de comandos do shell do Android. Funções puras.</summary>
public static class AndroidShellParser
{
    /// <summary>
    /// IP do Wi-Fi a partir de <c>ip route</c>: a rota da interface <c>wlan*</c> traz o IP do celular em <c>src</c>
    /// (ex: <c>192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.10</c>). Outras interfaces
    /// (dados móveis, VPN) são ignoradas.
    /// </summary>
    public static string? ParseWifiIpFromRoutes(string? output)
    {
        foreach (var line in Lines(output))
        {
            var tokens = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var device = ValueAfter(tokens, "dev");
            var source = ValueAfter(tokens, "src");
            if (device is not null && device.StartsWith("wlan", StringComparison.Ordinal) && IsIpv4(source))
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>
    /// IP a partir de <c>ip -f inet addr show wlan0</c> (linha <c>inet 192.0.2.10/24 ...</c>). Reserva do <c>ip route</c>.
    /// </summary>
    public static string? ParseInetAddress(string? output)
    {
        foreach (var line in Lines(output))
        {
            var tokens = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var address = ValueAfter(tokens, "inet")?.Split('/')[0];
            if (IsIpv4(address) && address != "127.0.0.1")
            {
                return address;
            }
        }

        return null;
    }

    /// <summary>Nome do aparelho (<c>settings get global device_name</c>). "null" ou vazio viram <c>null</c>.</summary>
    public static string? ParseDeviceName(string? output)
    {
        var name = output?.Trim();
        return string.IsNullOrEmpty(name) || name == "null" ? null : name;
    }

    private static IEnumerable<string> Lines(string? output) =>
        (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ValueAfter(string[] tokens, string key)
    {
        var index = Array.IndexOf(tokens, key);
        return index >= 0 && index + 1 < tokens.Length ? tokens[index + 1] : null;
    }

    private static bool IsIpv4(string? text) =>
        text is not null
        && text.Count(c => c == '.') == 3
        && IPAddress.TryParse(text, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork;
}
