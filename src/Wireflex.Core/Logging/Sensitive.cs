using System.Text.RegularExpressions;
using Wireflex.Core.Adb;

namespace Wireflex.Core.Logging;

/// <summary>
/// Mascara dados pessoais (serial e IP) antes de ir para logs ou para a tela:
/// só os últimos 4 caracteres ficam visíveis.
/// </summary>
public static partial class Sensitive
{
    private const string Prefix = "***";
    private const int Visible = 4;

    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(vazio)";
        }

        return value.Length <= Visible ? Prefix : Prefix + value[^Visible..];
    }

    /// <summary>
    /// Mascara um serial do adb de acordo com o formato: IP:porta, nome mDNS ou serial de USB.
    /// </summary>
    public static string MaskAdbSerial(string? adbSerial)
    {
        if (string.IsNullOrEmpty(adbSerial))
        {
            return Mask(adbSerial);
        }

        switch (AdbSerialClassifier.Classify(adbSerial))
        {
            case AdbTransport.WifiTcpip when AdbSerialClassifier.TryParseHostPort(adbSerial, out var host, out var port):
                return $"{Mask(host)}:{port}";
            case AdbTransport.WifiMdns when AdbSerialClassifier.TryExtractMdnsSerial(adbSerial, out var serial):
                return $"mdns:{Mask(serial)}";
            case AdbTransport.Emulator:
                return adbSerial;
            default:
                return Mask(adbSerial);
        }
    }

    /// <summary>
    /// Rede de segurança para textos livres (stderr do adb, exceções): mascara qualquer IPv4.
    /// </summary>
    public static string ScrubIpAddresses(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Ipv4Regex().Replace(text, match => Mask(match.Value));
    }

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex Ipv4Regex();
}
