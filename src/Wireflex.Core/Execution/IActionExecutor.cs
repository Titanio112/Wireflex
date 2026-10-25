using Wireflex.Core.Decisions;

namespace Wireflex.Core.Execution;

/// <summary>
/// Executa as ações decididas pelo <see cref="DecisionEngine"/>. O que acontece depois (scrcpy abriu,
/// usuário fechou a janela, scrcpy caiu) volta como <see cref="EngineInput"/> pelo callback recebido.
/// </summary>
public interface IActionExecutor : IAsyncDisposable
{
    Task ExecuteAsync(EngineAction action, CancellationToken cancellationToken = default);
}

/// <summary>Fala com o usuário: perguntas e avisos. A bandeja implementa na Etapa 3; o DevConsole escreve na tela.</summary>
public interface IUserNotifier
{
    /// <summary>Pergunta se deve abrir o espelhamento. A resposta volta como <see cref="UserAnswered"/>.</summary>
    void Ask(AskUser request);

    /// <summary>Avisa um problema no celular (unauthorized, offline).</summary>
    void Notify(NotifyUser notice);

    /// <summary>O scrcpy não abriu.</summary>
    void ScrcpyFailed(string serial, string message);
}

/// <summary>Não executa nada: o padrão do DevConsole, que só mostra as ações.</summary>
public sealed class SimulatedActionExecutor : IActionExecutor
{
    public Task ExecuteAsync(EngineAction action, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
