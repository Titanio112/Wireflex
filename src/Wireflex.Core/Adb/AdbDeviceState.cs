namespace Wireflex.Core.Adb;

/// <summary>
/// Estado de um aparelho como o adb informa. Os nomes seguem <c>to_string(ConnectionState)</c> do adb.cpp.
/// </summary>
public enum AdbDeviceState
{
    Unknown,
    Device,
    Offline,
    Unauthorized,
    Authorizing,
    Connecting,
    Bootloader,
    Recovery,
    Rescue,
    Sideload,
    Host,
    Detached,
    NoPermissions,
}

public static class AdbDeviceStates
{
    public static AdbDeviceState Parse(string? text) => text switch
    {
        "device" => AdbDeviceState.Device,
        "offline" => AdbDeviceState.Offline,
        "unauthorized" => AdbDeviceState.Unauthorized,
        "authorizing" => AdbDeviceState.Authorizing,
        "connecting" => AdbDeviceState.Connecting,
        "bootloader" => AdbDeviceState.Bootloader,
        "recovery" => AdbDeviceState.Recovery,
        "rescue" => AdbDeviceState.Rescue,
        "sideload" => AdbDeviceState.Sideload,
        "host" => AdbDeviceState.Host,
        "detached" => AdbDeviceState.Detached,
        "no permissions" => AdbDeviceState.NoPermissions,
        _ => AdbDeviceState.Unknown,
    };
}
