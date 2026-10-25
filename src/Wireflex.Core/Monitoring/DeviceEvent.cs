using Wireflex.Core.Adb;

namespace Wireflex.Core.Monitoring;

/// <summary>Evento gerado pelo <see cref="DeviceMonitor"/>.</summary>
public abstract record DeviceEvent;

/// <summary>Uma conexão nova apareceu (ex: cabo plugado, Wi-Fi conectado).</summary>
public sealed record DeviceAppeared(DeviceConnection Connection) : DeviceEvent;

/// <summary>Uma conexão sumiu.</summary>
public sealed record DeviceDisappeared(DeviceConnection Connection) : DeviceEvent;

/// <summary>Uma conexão mudou de estado (ex: unauthorized para device).</summary>
public sealed record DeviceStateChanged(DeviceConnection Connection, AdbDeviceState PreviousState) : DeviceEvent;

/// <summary>O melhor caminho até o celular mudou (ex: de USB para Wi-Fi ao tirar o cabo).</summary>
public sealed record DeviceTransportChanged(string PhoneKey, AdbTransport PreviousTransport, AdbTransport CurrentTransport) : DeviceEvent;

/// <summary>O acompanhamento do adb ficou disponível ou caiu.</summary>
public sealed record MonitorStatusChanged(bool AdbAvailable, string? Reason) : DeviceEvent;
