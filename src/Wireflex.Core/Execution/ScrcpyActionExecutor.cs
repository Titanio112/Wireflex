using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Decisions;
using Wireflex.Core.Logging;
using Wireflex.Core.Scrcpy;

namespace Wireflex.Core.Execution;

/// <summary>
/// Executa as ações de verdade: abre, troca e fecha o scrcpy (um por celular) e repassa perguntas e avisos.
/// </summary>
/// <remarks>
/// Quando o scrcpy termina sozinho, o código de saída diz o motivo: 0 vira <see cref="ScrcpyClosedByUser"/>,
/// o resto vira <see cref="ScrcpyExited"/>. Quando quem fecha é o próprio Wireflex (troca, pausa, encerramento),
/// nada é informado: o motor já sabe.
/// </remarks>
public sealed class ScrcpyActionExecutor : IActionExecutor
{
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IScrcpyLauncher _launcher;
    private readonly IUserNotifier _notifier;
    private readonly Func<string, string> _windowTitle;
    private readonly Action<EngineInput> _report;
    private readonly ILogger<ScrcpyActionExecutor> _logger;
    private readonly TimeSpan _stopTimeout;
    private readonly object _sync = new();
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="windowTitle">Título da janela a partir do serial do celular.</param>
    /// <param name="report">Recebe o que aconteceu com o scrcpy, para repassar ao motor.</param>
    public ScrcpyActionExecutor(
        IScrcpyLauncher launcher,
        IUserNotifier notifier,
        Func<string, string> windowTitle,
        Action<EngineInput> report,
        ILogger<ScrcpyActionExecutor>? logger = null,
        TimeSpan? stopTimeout = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _windowTitle = windowTitle ?? throw new ArgumentNullException(nameof(windowTitle));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _logger = logger ?? NullLogger<ScrcpyActionExecutor>.Instance;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
    }

    /// <summary>Celulares com scrcpy aberto agora.</summary>
    public IReadOnlyCollection<string> Mirroring
    {
        get
        {
            lock (_sync)
            {
                return _running.Keys.ToList();
            }
        }
    }

    public async Task ExecuteAsync(EngineAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Serial vem da base EngineAction (anulável); nestas ações ele sempre existe.
        switch (action)
        {
            case OpenScrcpy { Serial: { } serial } a:
                await StopAsync(serial, "já havia um scrcpy aberto").ConfigureAwait(false);
                Start(serial, a.AdbSerial);
                break;
            case SwitchScrcpy { Serial: { } serial } a:
                // O scrcpy não troca de conexão aberto: fecha e abre de novo pela outra.
                await StopAsync(serial, $"troca de {a.From} para {a.To}").ConfigureAwait(false);
                Start(serial, a.AdbSerial);
                break;
            case CloseScrcpy { Serial: { } serial } a:
                await StopAsync(serial, a.Reason).ConfigureAwait(false);
                break;
            case AskUser a:
                _notifier.Ask(a);
                break;
            case NotifyUser a:
                _notifier.Notify(a);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<string> serials;
        lock (_sync)
        {
            _disposed = true;
            serials = _running.Keys.ToList();
        }

        foreach (var serial in serials)
        {
            await StopAsync(serial, "Wireflex encerrando").ConfigureAwait(false);
        }
    }

    private void Start(string serial, string adbSerial)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }

        IScrcpyProcess process;
        try
        {
            process = _launcher.Start(new ScrcpyLaunchRequest(serial, adbSerial, _windowTitle(serial)));
        }
        catch (ScrcpyException ex)
        {
            _logger.LogWarning(ex, "scrcpy não abriu para {Phone}", Sensitive.Mask(serial));
            _notifier.ScrcpyFailed(serial, ex.Message);
            _report(new ScrcpyExited(serial, adbSerial));
            return;
        }

        var running = new Running(adbSerial, process);
        lock (_sync)
        {
            _running[serial] = running;
        }

        _report(new ScrcpyStarted(serial, adbSerial));
        _ = process.Exited.ContinueWith(t => OnExited(serial, running, t.Result), TaskScheduler.Default);
    }

    private void OnExited(string serial, Running running, int exitCode)
    {
        lock (_sync)
        {
            if (running.StoppedByUs || !_running.TryGetValue(serial, out var current) || current != running)
            {
                return;
            }

            _running.Remove(serial);
        }

        var kind = ScrcpyExit.Classify(exitCode);
        _logger.LogInformation("scrcpy de {Phone} terminou: código {Code} ({Kind})", Sensitive.Mask(serial), exitCode, kind);
        _report(kind == ScrcpyExitKind.ClosedByUser
            ? new ScrcpyClosedByUser(serial, running.AdbSerial)
            : new ScrcpyExited(serial, running.AdbSerial));
    }

    private async Task StopAsync(string serial, string reason)
    {
        Running? running;
        lock (_sync)
        {
            if (!_running.Remove(serial, out running))
            {
                return;
            }

            running.StoppedByUs = true;
        }

        _logger.LogInformation("Fechando o scrcpy de {Phone}: {Reason}", Sensitive.Mask(serial), reason);
        running.Process.Kill();
        try
        {
            await running.Process.Exited.WaitAsync(_stopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("O scrcpy de {Phone} não fechou em {Seconds} s", Sensitive.Mask(serial), _stopTimeout.TotalSeconds);
        }
    }

    private sealed class Running(string adbSerial, IScrcpyProcess process)
    {
        public string AdbSerial { get; } = adbSerial;

        public IScrcpyProcess Process { get; } = process;

        public bool StoppedByUs { get; set; }
    }
}
