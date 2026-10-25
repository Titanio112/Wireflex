using System.Globalization;

namespace Wireflex.Core.Adb;

/// <summary>
/// Interpreta a lista de aparelhos do adb, tanto a saída de <c>adb devices [-l]</c> quanto
/// cada mensagem de <c>adb track-devices [-l]</c>. Funções puras, sem processo nem rede.
/// </summary>
/// <remarks>
/// Formato (transport.cpp, <c>append_transport</c>):
/// curto: <c>SERIAL\tESTADO</c>;
/// longo: <c>SERIAL</c> alinhado em 22 colunas, <c>ESTADO</c> e depois, se existirem,
/// <c>usb:CAMINHO product:X model:Y device:Z transport_id:N</c>.
/// </remarks>
public static class AdbDeviceListParser
{
    private const string NoSerialNumber = "(no serial number)";
    private const string NoPermissions = "no permissions";
    private static readonly char[] Separators = [' ', '\t'];

    public static IReadOnlyList<AdbDevice> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var devices = new List<AdbDevice>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || IsNoise(line))
            {
                continue;
            }

            if (TryParseLine(line, out var device))
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    public static bool TryParseLine(string line, out AdbDevice device)
    {
        device = null!;

        string serial;
        string rest;
        if (line.StartsWith(NoSerialNumber, StringComparison.Ordinal))
        {
            serial = NoSerialNumber;
            rest = line[NoSerialNumber.Length..];
        }
        else
        {
            var split = line.IndexOfAny(Separators);
            if (split <= 0)
            {
                return false;
            }

            serial = line[..split];
            rest = line[split..];
        }

        rest = rest.TrimStart(Separators);
        if (rest.Length == 0)
        {
            return false;
        }

        AdbDeviceState state;
        string[] tokens;
        if (rest.StartsWith(NoPermissions, StringComparison.Ordinal))
        {
            // No Linux o estado vem com um texto de ajuda com espaços; os campos chave:valor vêm depois.
            state = AdbDeviceState.NoPermissions;
            tokens = rest[NoPermissions.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            var parts = rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            state = AdbDeviceStates.Parse(parts[0]);
            tokens = parts[1..];
        }

        string? usbPath = null;
        string? product = null;
        string? model = null;
        string? deviceName = null;
        long? transportId = null;

        foreach (var token in tokens)
        {
            if (TryGetValue(token, "usb:", out _))
            {
                usbPath = token;
            }
            else if (TryGetValue(token, "product:", out var value))
            {
                product = value;
            }
            else if (TryGetValue(token, "model:", out value))
            {
                model = value;
            }
            else if (TryGetValue(token, "device:", out value))
            {
                deviceName = value;
            }
            else if (TryGetValue(token, "transport_id:", out value)
                     && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                transportId = id;
            }
        }

        device = new AdbDevice(
            serial,
            state,
            AdbSerialClassifier.Classify(serial),
            model,
            product,
            deviceName,
            usbPath,
            transportId);
        return true;
    }

    private static bool IsNoise(string line) =>
        line.StartsWith("List of devices", StringComparison.Ordinal)
        || line.StartsWith('*')
        || line.StartsWith("adb server", StringComparison.Ordinal)
        || line.StartsWith("adb: ", StringComparison.Ordinal)
        || line.StartsWith("error:", StringComparison.Ordinal);

    private static bool TryGetValue(string token, string key, out string value)
    {
        if (token.StartsWith(key, StringComparison.Ordinal) && token.Length > key.Length)
        {
            value = token[key.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
