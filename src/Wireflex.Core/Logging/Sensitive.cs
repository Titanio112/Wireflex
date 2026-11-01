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
    private const int MinRememberedLength = 5;
    private const int MaxRemembered = 256;

    // Estados que o adb e o scrcpy escrevem depois do serial numa lista de aparelhos.
    private const string States = "device|offline|unauthorized|authorizing|connecting|recovery|rescue|sideload|bootloader|host|unknown|no permissions";

    private static readonly object RememberedSync = new();
    private static string[] _remembered = [];

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
    /// Rede de segurança para textos livres (logs, tela, saída do scrcpy e do adb, exceções): mascara os seriais
    /// já vistos (<see cref="RememberSerial"/>), os seriais reconhecidos pelo formato (<see cref="ScrubSerials"/>)
    /// e os IPs, e troca o nome de usuário do Windows em caminhos por <c>%USERNAME%</c>.
    /// </summary>
    public static string Scrub(string text) =>
        ScrubUserName(ScrubIpAddresses(ScrubSerials(ScrubRemembered(text))), Environment.UserName);

    /// <summary>
    /// Guarda um serial (do adb ou do celular) para ser mascarado em qualquer texto daqui em diante, mesmo onde
    /// o formato não denuncia que é um serial. Vale para o processo inteiro; valores curtos demais são ignorados.
    /// </summary>
    public static void RememberSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }

        switch (AdbSerialClassifier.Classify(serial))
        {
            case AdbTransport.Emulator:
            case AdbTransport.WifiTcpip:
                // Emulador não é dado pessoal; IP:porta já é mascarado pelo formato.
                return;
            case AdbTransport.WifiMdns when AdbSerialClassifier.TryExtractMdnsSerial(serial, out var mdnsSerial):
                serial = mdnsSerial;
                break;
        }

        if (serial.Length < MinRememberedLength)
        {
            return;
        }

        lock (RememberedSync)
        {
            if (_remembered.Length >= MaxRemembered || _remembered.Contains(serial, StringComparer.Ordinal))
            {
                return;
            }

            // Os mais longos primeiro: um serial que contém outro é trocado inteiro.
            string[] next = [.. _remembered.Append(serial).OrderByDescending(s => s.Length)];
            Volatile.Write(ref _remembered, next);
        }
    }

    /// <summary>Mascara os seriais guardados por <see cref="RememberSerial"/>.</summary>
    public static string ScrubRemembered(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var serial in Volatile.Read(ref _remembered))
        {
            if (text.Contains(serial, StringComparison.Ordinal))
            {
                text = text.Replace(serial, Mask(serial), StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>
    /// Mascara seriais reconhecidos pelo formato, mesmo os nunca vistos: linhas de lista de aparelhos
    /// (<c>(usb) SERIAL device</c> do scrcpy, <c>SERIAL&#9;device</c> do adb), mensagens do adb
    /// (<c>device 'SERIAL' not found</c>), argumentos (<c>-s</c>, <c>--serial</c>, <c>ANDROID_SERIAL</c>) e nomes mDNS.
    /// </summary>
    public static string ScrubSerials(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = MdnsNameRegex().Replace(text, match => MaskAdbSerial(match.Value));
        text = DeviceListRowRegex().Replace(text, match => MaskAdbSerial(match.Value));
        text = AdbDevicesRowRegex().Replace(text, match => MaskAdbSerial(match.Value));
        text = QuotedDeviceRegex().Replace(text, match => MaskAdbSerial(match.Value));
        return SerialArgumentRegex().Replace(text, match => MaskAdbSerial(match.Value));
    }

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

    // Nome de serviço mDNS do adb: adb-SERIAL-xxxxxx._adb-tls-connect._tcp (ou pairing).
    [GeneratedRegex(@"adb-[A-Za-z0-9]+-[A-Za-z0-9]+\._adb-tls-(?:connect|pairing)\._tcp\.?")]
    private static partial Regex MdnsNameRegex();

    // Lista do scrcpy: "(usb)  SERIAL   device  Modelo" (também tcpip). O que já tem *** fica como está.
    [GeneratedRegex(@"(?<=\((?:usb|tcpip|local|any)\)[ \t]+)[^\s*]+(?=[ \t]+(?:" + States + @")\b)")]
    private static partial Regex DeviceListRowRegex();

    // Lista do adb devices: "SERIAL<tab>device ..." no começo de uma linha.
    [GeneratedRegex(@"(?<=^[ \t]*)[^\s*]+(?=\t+(?:" + States + @")\b)", RegexOptions.Multiline)]
    private static partial Regex AdbDevicesRowRegex();

    // Mensagens do adb: device 'SERIAL' not found, no such device 'SERIAL'.
    [GeneratedRegex(@"(?<=\bdevice[ \t]+['""])(?!\*\*\*)[^'""\s]+(?=['""])")]
    private static partial Regex QuotedDeviceRegex();

    // Argumentos e variável: -s SERIAL, --serial SERIAL, --serial=SERIAL, ANDROID_SERIAL=SERIAL.
    [GeneratedRegex(@"(?<=(?:(?<!\S)-s[ \t]+|--serial[= \t]+|ANDROID_SERIAL=)['""]?)(?!\*\*\*)[^\s'""*]+")]
    private static partial Regex SerialArgumentRegex();

    // IPv6 completo (8 grupos) ou comprimido com "::" e pelo menos um grupo de cada lado.
    // Exige isso para não confundir com horários como 12:40:16.
    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:)+:(?:[0-9A-Fa-f]{1,4}:)*[0-9A-Fa-f]{1,4})(?![0-9A-Fa-f:])")]
    private static partial Regex Ipv6Regex();
}
