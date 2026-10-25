using Wireflex.Core.Adb;
using Wireflex.Core.Monitoring;

namespace Wireflex.Core.Decisions;

/// <summary>
/// Converte eventos do <see cref="DeviceMonitor"/> em entradas do <see cref="DecisionEngine"/>.
/// Ignora emuladores (o Wireflex é para celulares) e conexões Wi-Fi ainda sem serial: o motor nunca
/// identifica um celular pelo IP. Quando o monitor descobre o serial, a conexão "aparece" já identificada.
/// </summary>
public static class MonitorEventTranslator
{
    public static EngineInput? Translate(DeviceEvent deviceEvent) => deviceEvent switch
    {
        DeviceAppeared { Connection: var c } when IsPhone(c) =>
            new ConnectionAdded(c.Key, c.AdbSerial, c.Transport, c.State),
        DeviceStateChanged { Connection: var c } when IsPhone(c) =>
            new ConnectionStateChanged(c.Key, c.AdbSerial, c.State),
        DeviceDisappeared { Connection: var c } when IsPhone(c) =>
            new ConnectionRemoved(c.Key, c.AdbSerial),
        MonitorStatusChanged { AdbAvailable: true } =>
            new Reevaluate(null, "servidor do adb disponível"),
        _ => null,
    };

    private static bool IsPhone(DeviceConnection connection) => connection.Transport switch
    {
        AdbTransport.Emulator => false,
        AdbTransport.Usb => true,
        _ => connection.PhoneSerial is not null,
    };
}
