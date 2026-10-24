using Wireflex.Core.Adb;
using Wireflex.Core.Monitoring;

namespace Wireflex.Core.Decisions;

/// <summary>
/// Converte eventos do <see cref="DeviceMonitor"/> em entradas do <see cref="DecisionEngine"/>.
/// Emuladores são ignorados: o Wireflex é para celulares.
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

    private static bool IsPhone(DeviceConnection connection) => connection.Transport != AdbTransport.Emulator;
}
