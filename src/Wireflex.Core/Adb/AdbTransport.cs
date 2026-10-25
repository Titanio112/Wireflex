namespace Wireflex.Core.Adb;

/// <summary>
/// Por onde o adb enxerga o aparelho, deduzido do formato do serial do adb.
/// </summary>
public enum AdbTransport
{
    /// <summary>Cabo USB: o serial do adb é o serial do aparelho.</summary>
    Usb,

    /// <summary>Wi-Fi via <c>adb tcpip</c>/<c>adb connect</c> (Modo A): serial no formato <c>host:porta</c>.</summary>
    WifiTcpip,

    /// <summary>Wi-Fi via depuração sem fio + mDNS (Modo B): serial no formato <c>adb-SERIAL-xxxx._adb-tls-connect._tcp</c>.</summary>
    WifiMdns,

    /// <summary>Emulador (<c>emulator-NNNN</c>). Não é tratado como celular.</summary>
    Emulator,
}

public static class AdbTransports
{
    /// <summary>
    /// Ordem de preferência quando o mesmo celular aparece por mais de um caminho: cabo primeiro.
    /// </summary>
    public static int Priority(AdbTransport transport) => transport switch
    {
        AdbTransport.Usb => 0,
        AdbTransport.WifiTcpip => 1,
        AdbTransport.WifiMdns => 2,
        _ => 3,
    };

    public static bool IsWifi(AdbTransport transport) =>
        transport is AdbTransport.WifiTcpip or AdbTransport.WifiMdns;
}
