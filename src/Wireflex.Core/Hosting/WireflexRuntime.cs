using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Decisions;
using Wireflex.Core.Execution;
using Wireflex.Core.Monitoring;
using Wireflex.Core.Processes;
using Wireflex.Core.Scrcpy;

namespace Wireflex.Core.Hosting;

/// <summary>O que é preciso para montar o Wireflex. Usado pelo app de bandeja e pelo DevConsole.</summary>
public sealed class WireflexRuntimeSettings
{
    public required string AdbPath { get; init; }

    /// <summary>Obrigatório com <see cref="Real"/>.</summary>
    public string? ScrcpyPath { get; init; }

    /// <summary>Executa de verdade: abre o scrcpy, liga tcpip/connect. Sem isto, só decide (simulado).</summary>
    public bool Real { get; init; }

    /// <summary>Celulares conhecidos e preferências (grava na config, se tiver onde gravar).</summary>
    public required PhoneCatalog Catalog { get; init; }

    /// <summary>Recebe perguntas e avisos (só no modo real; no simulado as ações só são mostradas).</summary>
    public required IUserNotifier Notifier { get; init; }

    /// <summary>Acompanha eventos, ações e notas (tela do DevConsole, bandeja).</summary>
    public IConnectionObserver? Observer { get; init; }

    /// <summary>Modo de conexão efetivo de cada celular. Padrão: o do <see cref="Catalog"/>.</summary>
    public Func<string, ConnectionMode>? ResolveConnectionMode { get; init; }

    /// <summary>Amarra os scrcpy ao processo (Job Object no Windows).</summary>
    public IChildProcessGuard? Guard { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Diagnóstico: conferir o título de cada janela do scrcpy no log.</summary>
    public TimeSpan? WindowCheckDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Monitor do adb + motor + coordenador + executor, montados do mesmo jeito para o app e para o DevConsole.
/// </summary>
public sealed class WireflexRuntime
{
    private readonly DeviceMonitor _monitor;
    private ConnectionCoordinator? _coordinator;
    private int _started;

    public WireflexRuntime(WireflexRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Real && string.IsNullOrWhiteSpace(settings.ScrcpyPath))
        {
            throw new ArgumentException("O modo real precisa do caminho do scrcpy.", nameof(settings));
        }

        var loggerFactory = settings.LoggerFactory ?? NullLoggerFactory.Instance;
        var catalog = settings.Catalog;
        Catalog = catalog;
        Adb = new AdbClient(settings.AdbPath, loggerFactory.CreateLogger<AdbClient>());

        if (settings.Real)
        {
            Scrcpy = new ScrcpyActionExecutor(
                new ScrcpyLauncher(settings.ScrcpyPath!, settings.AdbPath, settings.Guard, loggerFactory.CreateLogger<ScrcpyLauncher>()),
                settings.Notifier,
                serial => "Wireflex - " + catalog.DisplayName(serial),
                input => _coordinator?.Post(input),
                loggerFactory.CreateLogger<ScrcpyActionExecutor>(),
                windowTitleForLog: serial => "Wireflex - " + catalog.DisplayNameForLog(serial),
                windowCheckDelay: settings.WindowCheckDelay);
        }

        _coordinator = new ConnectionCoordinator(
            new DecisionEngine(settings.ResolveConnectionMode ?? catalog.ResolveConnectionMode),
            Scrcpy ?? (IActionExecutor)new SimulatedActionExecutor(),
            Adb,
            catalog,
            new ConnectionCoordinatorOptions
            {
                PrepareWifi = settings.Real,
                ReconnectWifi = settings.Real,
                DisconnectWifiOnForget = settings.Real,
            },
            settings.Observer,
            loggerFactory);
        _monitor = new DeviceMonitor(Adb, new DeviceMonitorOptions(), loggerFactory.CreateLogger<DeviceMonitor>());
    }

    public ConnectionCoordinator Coordinator => _coordinator!;

    public PhoneCatalog Catalog { get; }

    public IAdbClient Adb { get; }

    /// <summary>O executor real (<c>null</c> no simulado).</summary>
    public ScrcpyActionExecutor? Scrcpy { get; }

    /// <summary>
    /// Roda o monitor e o coordenador até cancelar. Ao terminar, os scrcpy abertos já foram fechados.
    /// Se o coordenador falhar, o monitor também para.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("O Wireflex já foi iniciado.");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = _monitor.RunAsync(stop.Token);
        var coordinator = Task.Run(
            async () =>
            {
                try
                {
                    await Coordinator.RunAsync(_monitor.Events, stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    // Sem o coordenador não há quem consuma os eventos.
                    await stop.CancelAsync().ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        try
        {
            await Task.WhenAll(monitor, coordinator).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }
}
