using Wireflex.Core.Adb;

namespace Wireflex.Core.Decisions;

/// <summary>
/// O que o <see cref="DecisionEngine"/> decidiu. Quem executa é outra camada (Etapa 2).
/// </summary>
public abstract record EngineAction(string? Serial);

/// <summary>Abrir o scrcpy para o celular usando esta conexão.</summary>
public sealed record OpenScrcpy(string Serial, string AdbSerial, AdbTransport Transport) : EngineAction(Serial);

/// <summary>Trocar o scrcpy aberto para outra conexão (ex: cabo saiu e o Wi-Fi está disponível).</summary>
public sealed record SwitchScrcpy(string Serial, string AdbSerial, AdbTransport From, AdbTransport To) : EngineAction(Serial);

/// <summary>Fechar o scrcpy deste celular.</summary>
public sealed record CloseScrcpy(string Serial, string Reason) : EngineAction(Serial);

/// <summary>Perguntar ao usuário se deve abrir o espelhamento (modo "perguntar").</summary>
public sealed record AskUser(string Serial, string AdbSerial, AdbTransport Transport) : EngineAction(Serial);

/// <summary>Avisar o usuário sobre um problema no celular, sem tentar abrir nada.</summary>
public sealed record NotifyUser(string Serial, DeviceNotice Notice) : EngineAction(Serial);

/// <summary>Nada a fazer. O motivo existe para log e para a ferramenta de teste.</summary>
public sealed record DoNothing(string? Serial, string Reason) : EngineAction(Serial);

public enum DeviceNotice
{
    /// <summary>O celular está pedindo para autorizar a depuração USB.</summary>
    Unauthorized,

    /// <summary>O adb vê o celular, mas ele não responde.</summary>
    Offline,
}
