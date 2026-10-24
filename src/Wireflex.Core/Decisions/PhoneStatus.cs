using Wireflex.Core.Adb;

namespace Wireflex.Core.Decisions;

/// <summary>Foto do estado de um celular dentro do motor, para exibir.</summary>
public sealed record PhoneStatus(
    string Serial,
    PhonePhase Phase,
    AdbTransport? ScrcpyTransport,
    IReadOnlyList<PhoneConnectionStatus> Connections,
    bool Paused);

public sealed record PhoneConnectionStatus(string AdbSerial, AdbTransport Transport, AdbDeviceState State);

public enum PhonePhase
{
    /// <summary>Sem USB e sem Wi-Fi utilizáveis.</summary>
    OutOfRange,

    /// <summary>Ao alcance, mas sem conexão utilizável (ex: unauthorized).</summary>
    Unavailable,

    /// <summary>Pausado pelo usuário.</summary>
    Paused,

    /// <summary>Espelhando (scrcpy aberto).</summary>
    Mirroring,

    /// <summary>Esperando o usuário responder se deve abrir.</summary>
    WaitingForAnswer,

    /// <summary>O usuário fechou o scrcpy (ou recusou); só reabre ao sair do alcance e voltar.</summary>
    ClosedByUser,

    /// <summary>Disponível, sem nada aberto (ex: modo "nunca").</summary>
    Idle,
}
