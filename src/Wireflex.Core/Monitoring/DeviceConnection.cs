using Wireflex.Core.Adb;

namespace Wireflex.Core.Monitoring;

/// <summary>
/// Uma conexão do adb já associada ao celular.
/// </summary>
/// <param name="AdbSerial">Serial do adb (USB: serial; Wi-Fi: <c>IP:porta</c> ou nome mDNS).</param>
/// <param name="PhoneSerial">Serial real do celular; <c>null</c> se ainda não deu para descobrir.</param>
public sealed record DeviceConnection(
    string AdbSerial,
    string? PhoneSerial,
    AdbTransport Transport,
    AdbDeviceState State,
    string? Model)
{
    /// <summary>Identidade do celular. Cai para o serial do adb só enquanto o real não é conhecido.</summary>
    public string Key => PhoneSerial ?? AdbSerial;
}
