using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Decisions;
using Wireflex.Core.Logging;
using Wireflex.Core.Monitoring;

namespace Wireflex.DevConsole;

/// <summary>
/// Textos em português para a tela. Todo serial e IP passa por <see cref="Id"/> (só os últimos 4 caracteres).
/// </summary>
internal static class Format
{
    /// <summary>Mascara serial de celular, serial do adb, IP:porta ou nome mDNS.</summary>
    public static string Id(string? serial) => Sensitive.MaskAdbSerial(serial);

    public static string Transport(AdbTransport transport) => EngineActionText.Transport(transport);

    public static string State(AdbDeviceState state) => state switch
    {
        AdbDeviceState.NoPermissions => "no permissions",
        AdbDeviceState.Unknown => "desconhecido",
        _ => state.ToString().ToLowerInvariant(),
    };

    public static string Mode(ConnectionMode mode) => mode switch
    {
        ConnectionMode.Automatic => "automático",
        ConnectionMode.Ask => "perguntar",
        ConnectionMode.Never => "nunca",
        _ => mode.ToString(),
    };

    public static string Phase(PhonePhase phase) => phase switch
    {
        PhonePhase.OutOfRange => "fora do alcance",
        PhonePhase.Unavailable => "indisponível",
        PhonePhase.Paused => "pausado",
        PhonePhase.Mirroring => "espelhando",
        PhonePhase.WaitingForAnswer => "aguardando resposta",
        PhonePhase.ClosedByUser => "fechado pelo usuário",
        PhonePhase.Idle => "disponível",
        _ => phase.ToString(),
    };

    public static string Event(DeviceEvent deviceEvent) => deviceEvent switch
    {
        DeviceAppeared { Connection: var c } =>
            $"Apareceu: {Connection(c)}{Model(c)}, estado {State(c.State)}",
        DeviceDisappeared { Connection: var c } =>
            $"Sumiu: {Connection(c)}",
        DeviceStateChanged { Connection: var c, PreviousState: var before } =>
            $"Mudou de estado: {Connection(c)}: {State(before)} -> {State(c.State)}",
        DeviceTransportChanged t =>
            $"Mudou de transporte: {Id(t.PhoneKey)} de {Transport(t.PreviousTransport)} para {Transport(t.CurrentTransport)}",
        MonitorStatusChanged { AdbAvailable: true } =>
            "adb disponível: acompanhando os aparelhos",
        MonitorStatusChanged m =>
            $"adb indisponível ({m.Reason}); tentando de novo (na hora e depois com espera crescente)",
        _ => deviceEvent.GetType().Name,
    };

    /// <summary>O mesmo texto que vai para o log (<see cref="EngineActionText"/>); na pergunta, diz como responder.</summary>
    public static string Action(EngineAction action) => action is AskUser
        ? EngineActionText.Describe(action) + ": responda com 's' (sim) ou 'n' (não)"
        : EngineActionText.Describe(action);

    public static string Status(int number, PhoneStatus phone)
    {
        var scrcpy = phone.ScrcpyTransport is { } t ? $", scrcpy via {Transport(t)}" : string.Empty;
        var paused = phone.Paused ? ", pausado" : string.Empty;
        var connections = phone.Connections.Count == 0
            ? "nenhuma conexão"
            : string.Join("; ", phone.Connections.Select(c => $"{Transport(c.Transport)} {Id(c.AdbSerial)} {State(c.State)}"));
        return $"  {number}. {Id(phone.Serial)}: {Phase(phone.Phase)}{scrcpy}{paused} | {connections}";
    }

    private static string Connection(DeviceConnection c)
    {
        var phone = Id(c.Key);
        var adb = Id(c.AdbSerial);
        return phone == adb
            ? $"{phone} via {Transport(c.Transport)}"
            : $"{phone} via {Transport(c.Transport)} ({adb})";
    }

    private static string Model(DeviceConnection c) =>
        string.IsNullOrWhiteSpace(c.Model) ? string.Empty : $" ({c.Model})";
}
