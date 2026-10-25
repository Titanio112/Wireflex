using Wireflex.Core.Adb;

namespace Wireflex.Core.Decisions;

/// <summary>Algo que aconteceu e que o <see cref="DecisionEngine"/> precisa considerar.</summary>
public abstract record EngineInput;

/// <summary>Uma conexão até o celular apareceu (USB = cabo conectou; Wi-Fi = Wi-Fi conectou).</summary>
public sealed record ConnectionAdded(string Serial, string AdbSerial, AdbTransport Transport, AdbDeviceState State) : EngineInput;

/// <summary>Uma conexão sumiu (USB = cabo desconectou; Wi-Fi = Wi-Fi desconectou).</summary>
public sealed record ConnectionRemoved(string Serial, string AdbSerial) : EngineInput;

/// <summary>Uma conexão mudou de estado (ex: unauthorized para device).</summary>
public sealed record ConnectionStateChanged(string Serial, string AdbSerial, AdbDeviceState State) : EngineInput;

/// <summary>O scrcpy abriu para este celular por esta conexão.</summary>
public sealed record ScrcpyStarted(string Serial, string AdbSerial) : EngineInput;

/// <summary>O usuário fechou a janela do scrcpy.</summary>
public sealed record ScrcpyClosedByUser(string Serial, string AdbSerial) : EngineInput;

/// <summary>O scrcpy terminou sem ser pelo usuário (erro, conexão caiu, foi fechado pelo próprio app).</summary>
public sealed record ScrcpyExited(string Serial, string AdbSerial) : EngineInput;

/// <summary>O usuário pausou (ou retomou) este celular.</summary>
public sealed record PhonePauseChanged(string Serial, bool Paused) : EngineInput;

/// <summary>Resposta do usuário à pergunta "abrir o espelhamento?".</summary>
public sealed record UserAnswered(string Serial, bool Accepted) : EngineInput;

/// <summary>
/// Pede para reavaliar um celular (ou todos, se <paramref name="Serial"/> for <c>null</c>).
/// Ex: configuração mudou, servidor do adb voltou.
/// </summary>
public sealed record Reevaluate(string? Serial, string Reason) : EngineInput;
