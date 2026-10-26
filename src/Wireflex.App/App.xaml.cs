using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Wireflex.Core;
using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Decisions;
using Wireflex.Core.Hosting;
using Wireflex.Core.Logging;
using Wireflex.Core.Scrcpy;
using Wireflex.Core.Tray;
using Wireflex.Windows;

namespace Wireflex.App;

/// <summary>
/// App de bandeja: sem janela principal. Monta o Wireflex (o mesmo do DevConsole com <c>--real</c>), mostra o ícone
/// na bandeja e, ao sair, fecha os scrcpy abertos.
/// </summary>
public partial class App : Application
{
    /// <summary>Acompanha e mostra, sem executar nem gravar nada (como o DevConsole sem <c>--real</c>).</summary>
    private const string SimulatedOption = "--simulado";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stop = new();
    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;
    private TrayIcon? _tray;
    private AskController? _questions;
    private JobObjectProcessGuard? _guard;
    private SingleInstance? _instance;
    private Task? _run;
    private int _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Já tem um Wireflex rodando nesta sessão: avisa ele (que mostra uma notificação) e fecha.
        _instance = SingleInstance.TryAcquire(AppInfo.Name);
        if (_instance is null)
        {
            SingleInstance.SignalExisting(AppInfo.Name);
            Shutdown();
            return;
        }

        _instance.ActivationRequested += () => _tray?.ShowNotification(
            AppInfo.Name,
            "O Wireflex já está rodando. Use o ícone na bandeja.");

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new FileLoggerProvider(WireflexPaths.LogsDirectory, LogLevel.Information));
        });
        _logger = _loggerFactory.CreateLogger<App>();
        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) => _ = ExitAsync();

        try
        {
            await StartAsync(simulated: e.Args.Contains(SimulatedOption, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "O Wireflex não conseguiu iniciar");
            MessageBox.Show($"O Wireflex não conseguiu iniciar: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private async Task StartAsync(bool simulated)
    {
        var loggerFactory = _loggerFactory!;
        _tray = new TrayIcon(Dispatcher, TrayIcon.LoadIcon(), () => _ = ExitAsync());

        // Simulado: lê a config sem nunca gravar e não executa nada (para testar a bandeja com celulares ligados).
        var store = new JsonConfigStore(WireflexPaths.ConfigDirectory, loggerFactory.CreateLogger<JsonConfigStore>());
        var config = simulated ? await store.TryReadAsync() ?? new WireflexConfig() : await store.LoadAsync();
        var catalog = new PhoneCatalog(config, simulated ? null : store, loggerFactory.CreateLogger<PhoneCatalog>());

        var adb = ToolPaths.Resolve(null, config.Global.AdbPath, AdbLocator.Find);
        var scrcpy = ToolPaths.Resolve(null, config.Global.ScrcpyPath, ScrcpyLocator.Find);
        if (adb is null || (scrcpy is null && !simulated))
        {
            var missing = adb is null ? "adb" : "scrcpy";
            _logger!.LogError("{Tool} não encontrado na configuração nem no PATH", missing);
            _tray.StatusMessage = $"{missing} não encontrado na configuração nem no PATH";
            _tray.RequestRefresh();
            _tray.ShowNotification(AppInfo.Name, $"{missing} não encontrado na configuração nem no PATH.", warning: true);
            return;
        }

        _logger!.LogInformation(
            "Wireflex iniciando{Mode}. adb: {Adb} ({AdbSource}); scrcpy: {Scrcpy}",
            simulated ? " no modo simulado (nada é executado nem gravado)" : string.Empty,
            Sensitive.Scrub(adb.Path),
            adb.Source,
            scrcpy is null ? "não encontrado" : $"{Sensitive.Scrub(scrcpy.Path)} ({scrcpy.Source})");

        // Os scrcpy ficam amarrados a este processo: se ele morrer (até à força), eles morrem junto.
        _guard = simulated ? null : new JobObjectProcessGuard();
        var runtime = new WireflexRuntime(new WireflexRuntimeSettings
        {
            AdbPath = adb.Path,
            ScrcpyPath = scrcpy?.Path,
            Real = !simulated,
            Catalog = catalog,
            Notifier = new TrayNotifier(Dispatcher, () => _tray, () => _questions, catalog.DisplayName),
            Observer = new TrayObserver(() => _tray),
            Guard = _guard,
            LoggerFactory = loggerFactory,
        });
        _tray.Commands = new TrayCommands(runtime.Coordinator, catalog);
        _questions = new AskController(
            () => _tray?.Commands,
            catalog.DisplayName,
            serial => runtime.Coordinator.GetStatus().Any(p => p.Serial == serial && p.Phase == PhonePhase.WaitingForAnswer));
        _tray.Refreshed += _questions.Refresh;
        _tray.OnError = ex => _logger?.LogError(ex, "Falha num comando do menu");
        _tray.MenuSource = () => TrayMenuModel.Build(runtime.Coordinator.GetStatus(), catalog.Phones, catalog.DisplayName, catalog.GlobalConnectionMode);
        _tray.RequestRefresh();
        _run = Task.Run(() => RunAsync(runtime));
    }

    private async Task RunAsync(WireflexRuntime runtime)
    {
        try
        {
            await runtime.RunAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger!.LogError(ex, "O acompanhamento dos celulares parou");
            _tray?.ShowNotification(AppInfo.Name, "O acompanhamento dos celulares parou. Veja o log e abra o Wireflex de novo.", warning: true);
        }
    }

    /// <summary>Para o monitor e o coordenador (que fecha os scrcpy) e encerra o app. Pode ser chamado mais de uma vez.</summary>
    private async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0)
        {
            return;
        }

        _logger?.LogInformation("Wireflex encerrando");
        await _stop.CancelAsync();
        if (_run is not null && await Task.WhenAny(_run, Task.Delay(StopTimeout)) != _run)
        {
            _logger?.LogWarning("O encerramento passou de {Seconds} s; o Job Object fecha os scrcpy", StopTimeout.TotalSeconds);
        }

        _tray?.Dispose();
        _guard?.Dispose();
        _loggerFactory?.Dispose();
        _instance?.Dispose();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Erro inesperado na interface");
        e.Handled = true;
    }
}
