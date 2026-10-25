namespace Wireflex.Core.Configuration;

/// <summary>O que fazer quando um celular fica disponível.</summary>
public enum ConnectionMode
{
    /// <summary>Abre o espelhamento sozinho.</summary>
    Automatic,

    /// <summary>Pergunta antes de abrir.</summary>
    Ask,

    /// <summary>Nunca abre sozinho.</summary>
    Never,
}

/// <summary>Como conectar por Wi-Fi.</summary>
public enum WifiMode
{
    /// <summary>O app escolhe.</summary>
    Automatic,

    /// <summary>Modo A: <c>adb tcpip 5555</c>, porta fixa, perde ao reiniciar o celular.</summary>
    Tcpip,

    /// <summary>Modo B: depuração sem fio + mDNS (Android 11+), porta variável.</summary>
    WirelessDebugging,
}

/// <summary>
/// Raiz do <c>config.json</c>. Sempre grave com <see cref="SchemaVersion"/> = <see cref="CurrentSchemaVersion"/>;
/// versões antigas passam pelo <see cref="ConfigMigrator"/> ao carregar.
/// </summary>
public sealed class WireflexConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GlobalSettings Global { get; set; } = new();

    public List<KnownPhone> Phones { get; set; } = [];

    public KnownPhone? FindPhone(string serial) =>
        Phones.FirstOrDefault(p => string.Equals(p.Serial, serial, StringComparison.Ordinal));

    /// <summary>Modo de conexão efetivo: o do celular, se definido; senão o global.</summary>
    public ConnectionMode ResolveConnectionMode(string serial) =>
        FindPhone(serial)?.Settings.ConnectionMode ?? Global.ConnectionMode;

    /// <summary>Modo de Wi-Fi efetivo: o do celular, se definido; senão o global.</summary>
    public WifiMode ResolveWifiMode(string serial) =>
        FindPhone(serial)?.Settings.WifiMode ?? Global.WifiMode;
}

public sealed class GlobalSettings
{
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.Automatic;

    public WifiMode WifiMode { get; set; } = WifiMode.Automatic;

    public bool StartWithWindows { get; set; }

    /// <summary>Caminho do adb.exe (ou da pasta dele). Vazio: procura no PATH.</summary>
    public string? AdbPath { get; set; }
}

/// <summary>Celular aprendido. A identidade é sempre o serial, nunca o IP.</summary>
public sealed class KnownPhone
{
    public string Serial { get; set; } = string.Empty;

    public string? FriendlyName { get; set; }

    public string? Model { get; set; }

    /// <summary>Só uma dica para reconectar por Wi-Fi; o IP muda por DHCP.</summary>
    public string? LastKnownIp { get; set; }

    public PhoneSettings Settings { get; set; } = new();
}

/// <summary>Configurações do celular. Valor <c>null</c> = usa o global.</summary>
public sealed class PhoneSettings
{
    public ConnectionMode? ConnectionMode { get; set; }

    public WifiMode? WifiMode { get; set; }
}
