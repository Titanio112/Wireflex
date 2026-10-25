using Wireflex.Core.Decisions;
using Wireflex.Core.Execution;
using Wireflex.Core.Scrcpy;

namespace Wireflex.Tests.Fakes;

/// <summary>scrcpy falso: registra cada abertura; o teste decide quando e como o processo termina.</summary>
internal sealed class FakeScrcpyLauncher : IScrcpyLauncher
{
    private readonly List<FakeScrcpyProcess> _started = [];
    private int _nextId = 1000;

    /// <summary>Se preenchido, <see cref="Start"/> falha com esta mensagem.</summary>
    public string? FailWith { get; set; }

    public IReadOnlyList<FakeScrcpyProcess> Started
    {
        get
        {
            lock (_started)
            {
                return _started.ToList();
            }
        }
    }

    public IScrcpyProcess Start(ScrcpyLaunchRequest request)
    {
        if (FailWith is not null)
        {
            throw new ScrcpyException(FailWith);
        }

        var process = new FakeScrcpyProcess(Interlocked.Increment(ref _nextId), request);
        lock (_started)
        {
            _started.Add(process);
        }

        return process;
    }
}

internal sealed class FakeScrcpyProcess(int id, ScrcpyLaunchRequest request) : IScrcpyProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; } = id;

    public ScrcpyLaunchRequest Request { get; } = request;

    public bool Killed { get; private set; }

    public Task<int> Exited => _exited.Task;

    public bool Running => !_exited.Task.IsCompleted;

    /// <summary>Título que a janela "mostra". Padrão: o pedido.</summary>
    public string? Title { get; set; }

    public string ReadWindowTitle() => Running ? Title ?? Request.WindowTitle : string.Empty;

    /// <summary>Simula o processo terminando sozinho (0 = usuário fechou, 2 = celular desconectou).</summary>
    public void Exit(int code) => _exited.TrySetResult(code);

    public void Kill()
    {
        Killed = true;
        _exited.TrySetResult(-1);
    }
}

/// <summary>Guarda perguntas e avisos.</summary>
internal sealed class RecordingNotifier : IUserNotifier
{
    public List<AskUser> Questions { get; } = [];

    public List<NotifyUser> Notices { get; } = [];

    public List<string> Failures { get; } = [];

    public void Ask(AskUser request) => Questions.Add(request);

    public void Notify(NotifyUser notice) => Notices.Add(notice);

    public void ScrcpyFailed(string serial, string message) => Failures.Add(message);
}
