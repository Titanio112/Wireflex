using Wireflex.Core.Adb;
using Wireflex.Core.Logging;

namespace Wireflex.Core.Decisions;

/// <summary>
/// Texto em português de cada ação do motor, igual no log e na tela do DevConsole. Serial e IP já saem
/// mascarados (só os últimos 4 caracteres).
/// </summary>
public static class EngineActionText
{
    public static string Transport(AdbTransport transport) => transport switch
    {
        AdbTransport.Usb => "USB",
        AdbTransport.WifiTcpip => "Wi-Fi (tcpip)",
        AdbTransport.WifiMdns => "Wi-Fi (mDNS)",
        AdbTransport.Emulator => "emulador",
        _ => transport.ToString(),
    };

    public static string Describe(EngineAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action switch
        {
            OpenScrcpy a =>
                $"AbrirScrcpy {Id(a.Serial)} via {Transport(a.Transport)} (conexão {Id(a.AdbSerial)})",
            SwitchScrcpy a =>
                $"TrocarScrcpy {Id(a.Serial)} de {Transport(a.From)} para {Transport(a.To)} (conexão {Id(a.AdbSerial)})",
            CloseScrcpy a =>
                $"FecharScrcpy {Id(a.Serial)}: {a.Reason}",
            AskUser a =>
                $"PerguntarAoUsuario {Id(a.Serial)} via {Transport(a.Transport)}",
            NotifyUser { Notice: DeviceNotice.Unauthorized } a =>
                $"Avisar {Id(a.Serial)}: o celular está pedindo para autorizar a depuração USB (unauthorized)",
            NotifyUser { Notice: DeviceNotice.Offline } a =>
                $"Avisar {Id(a.Serial)}: o adb vê o celular, mas ele não responde (offline)",
            NotifyUser a =>
                $"Avisar {Id(a.Serial)}: {a.Notice}",
            DoNothing a =>
                $"NaoFazerNada{(a.Serial is null ? string.Empty : " " + Id(a.Serial))}: {a.Reason}",
            _ => action.GetType().Name,
        };
    }

    private static string Id(string? serial) => Sensitive.MaskAdbSerial(serial);
}
