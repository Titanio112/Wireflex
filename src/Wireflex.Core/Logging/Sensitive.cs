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
    /// Rede de segurança para textos livres (logs, tela, stderr do adb, exceções):
    /// mascara IPs e troca o nome de usuário do Windows em caminhos por <c>%USERNAME%</c>.
    /// </summary>
    public static string Scrub(string text) => ScrubUserName(ScrubIpAddresses(text), Environment.UserName);

    /// <summary>
    /// <see cref="Scrub"/> e, além disso, mascara os valores conhecidos (ex: seriais) onde aparecerem.
    /// Para textos de terceiros, como a saída do scrcpy, que repetem o serial do aparelho.
    /// </summary>
    public static string ScrubKnown(string text, params string?[] knownValues)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var value in knownValues.Where(v => !string.IsNullOrEmpty(v)).Distinct().OrderByDescending(v => v!.Length))
        {
            text = text.Replace(value!, MaskAdbSerial(value), StringComparison.Ordinal);
        }

        return Scrub(text);
    }

    /// <summary>Mascara qualquer IPv4 (inclusive no fim de frase) e IPv6.</summary>
    public static string ScrubIpAddresses(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var withoutV4 = Ipv4Regex().Replace(text, match => Mask(match.Value));
        return Ipv6Regex().Replace(withoutV4, match => Mask(match.Value));
    }

    /// <summary>
    /// Troca o nome de usuário quando ele aparece como pasta de um caminho
    /// (ex: <c>C:\Users\nome\AppData</c> vira <c>C:\Users\%USERNAME%\AppData</c>).
    /// </summary>
    public static string ScrubUserName(string text, string? userName)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(userName))
        {
            return text;
        }

        var pattern = $@"(?<=[\\/]){Regex.Escape(userName)}(?=[\\/]|$|[\s""'])";
        return Regex.Replace(text, pattern, "%USERNAME%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // Quatro octetos, sem dígito antes ou depois. Um ponto final de frase é aceito; um quinto octeto, não.
    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?!\.?\d)")]
    private static partial Regex Ipv4Regex();

    // IPv6 completo (8 grupos) ou comprimido com "::" e pelo menos um grupo de cada lado.
    // Exige isso para não confundir com horários como 12:40:16.
    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:)+:(?:[0-9A-Fa-f]{1,4}:)*[0-9A-Fa-f]{1,4})(?![0-9A-Fa-f:])")]
    private static partial Regex Ipv6Regex();
}
