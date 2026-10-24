using System.Globalization;

namespace Wireflex.Core.Adb;

/// <summary>
/// Interpreta o serial que o adb usa para cada conexão. Funções puras.
/// </summary>
public static class AdbSerialClassifier
{
    public const string MdnsTlsConnectService = "._adb-tls-connect._tcp";
    public const string MdnsLegacyService = "._adb._tcp";
    private const string MdnsInstancePrefix = "adb-";

    public static AdbTransport Classify(string serial)
    {
        ArgumentNullException.ThrowIfNull(serial);

        if (TryGetMdnsInstance(serial, out _))
        {
            return AdbTransport.WifiMdns;
        }

        if (serial.StartsWith("emulator-", StringComparison.Ordinal))
        {
            return AdbTransport.Emulator;
        }

        return TryParseHostPort(serial, out _, out _) ? AdbTransport.WifiTcpip : AdbTransport.Usb;
    }

    /// <summary>
    /// Separa <c>host:porta</c>, incluindo IPv6 entre colchetes (<c>[fe80::1]:5555</c>).
    /// </summary>
    public static bool TryParseHostPort(string serial, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var colon = serial.LastIndexOf(':');
        if (colon <= 0 || colon == serial.Length - 1)
        {
            return false;
        }

        var hostPart = serial[..colon];
        var portPart = serial[(colon + 1)..];

        if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)
            || parsedPort is < 1 or > 65535)
        {
            return false;
        }

        if (hostPart.StartsWith('[') && hostPart.EndsWith(']'))
        {
            hostPart = hostPart[1..^1];
        }
        else if (hostPart.Contains(':'))
        {
            // IPv6 sem colchetes não é um formato que o adb usa.
            return false;
        }

        if (hostPart.Length == 0 || hostPart.Any(char.IsWhiteSpace))
        {
            return false;
        }

        host = hostPart;
        port = parsedPort;
        return true;
    }

    /// <summary>
    /// Extrai o serial do celular de um nome mDNS como <c>adb-SERIAL-xyMD0H._adb-tls-connect._tcp</c>.
    /// </summary>
    public static bool TryExtractMdnsSerial(string serial, out string phoneSerial)
    {
        phoneSerial = string.Empty;
        if (!TryGetMdnsInstance(serial, out var instance)
            || !instance.StartsWith(MdnsInstancePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = instance[MdnsInstancePrefix.Length..];
        var lastDash = rest.LastIndexOf('-');
        var candidate = lastDash > 0 ? rest[..lastDash] : rest;
        if (candidate.Length == 0)
        {
            return false;
        }

        phoneSerial = candidate;
        return true;
    }

    private static bool TryGetMdnsInstance(string serial, out string instance)
    {
        // Algumas versões do adb deixam um ponto no final do nome mDNS.
        var trimmed = serial.TrimEnd('.');
        foreach (var service in (ReadOnlySpan<string>)[MdnsTlsConnectService, MdnsLegacyService])
        {
            if (trimmed.EndsWith(service, StringComparison.Ordinal) && trimmed.Length > service.Length)
            {
                instance = trimmed[..^service.Length];
                return true;
            }
        }

        instance = string.Empty;
        return false;
    }
}
