using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Wireflex.Core;
using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Decisions;
using Wireflex.Core.Execution;
using Wireflex.Core.Logging;
using Wireflex.Core.Monitoring;
using Wireflex.Core.Scrcpy;
using Wireflex.DevConsole;
using Wireflex.Windows;

// Ferramenta de teste manual: monitor real do adb + motor de decisão + coordenador de conexão.
// Padrão simulado (só lê e mostra). Com --real executa: abre o scrcpy, liga tcpip/connect e grava a config.

Console.OutputEncoding = Encoding.UTF8;
var output = new ConsoleOutput();

if (!CommandLineOptions.TryParse(args, out var options, out var argumentError))
{
    if (argumentError is not null)
    {
        output.Error(argumentError);
    }

    output.Plain(CommandLineOptions.Usage);
    return argumentError is null ? 0 : 2;
}

var logLevel = options.Verbose ? LogLevel.Debug : LogLevel.Information;
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddProvider(new FileLoggerProvider(WireflexPaths.LogsDirectory, logLevel));
    builder.AddProvider(new ConsoleLoggerProvider(output, options.Verbose ? LogLevel.Information : LogLevel.Warning));
});

// Simulado: lê o config.json real, se existir, sem nunca gravar. Real: carrega (cria/migra) e grava o que aprender.
var configStore = new JsonConfigStore(WireflexPaths.ConfigDirectory, loggerFactory.CreateLogger<JsonConfigStore>());
var stored = options.Real ? await configStore.LoadAsync() : await configStore.TryReadAsync();

// Cópia da sessão: --modo e o comando m mudam só esta, nunca o arquivo.
var config = (options.Real ? await configStore.TryReadAsync() : stored) is { } read ? Clone(read) : new WireflexConfig();
if (options.ConnectionMode is { } mode)
{
    config.Global.ConnectionMode = mode;
}

if (!TryResolve("adb", options.AdbPath, config.Global.AdbPath, AdbLocator.Find, out var adbPath, out var adbSource))
{
    output.Error(options.AdbPath is not null
        ? $"adb não encontrado em: {options.AdbPath}"
        : "adb não encontrado na configuração nem no PATH. Use --adb <caminho do adb.exe>.");
    return 1;
}

output.Info($"adb ({adbSource}): {adbPath}");

var hasScrcpy = TryResolve("scrcpy", options.ScrcpyPath, config.Global.ScrcpyPath, ScrcpyLocator.Find, out var scrcpyPath, out var scrcpySource);
if (hasScrcpy)
{
    output.Info($"scrcpy ({scrcpySource}): {scrcpyPath}");
}
else if (options.Real)
{
    output.Error(options.ScrcpyPath is not null
        ? $"scrcpy não encontrado em: {options.ScrcpyPath}"
        : "scrcpy não encontrado na configuração nem no PATH. Use --scrcpy <caminho do scrcpy.exe>.");
    return 1;
}
else
{
    output.Warning("scrcpy não encontrado (no modo simulado não faz falta).");
}

if (options.Real)
{
    output.Warning("MODO REAL: abre o scrcpy, liga o modo TCP/IP, conecta pelo Wi-Fi e grava %APPDATA%\\Wireflex\\config.json.");
}
else
{
    output.Info(stored is null
        ? "Modo simulado. Configuração: padrão, só em memória (nada é gravado)"
        : "Modo simulado. Configuração: lida de %APPDATA%\\Wireflex\\config.json, só em memória (nada é gravado)");
}

output.Info($"Modo de conexão global: {Format.Mode(config.Global.ConnectionMode)}");
output.Info("Logs: %LOCALAPPDATA%\\Wireflex\\logs (serial e IP mascarados)");
output.Info("Monitor ligado. Digite h para ver os comandos. Ctrl+C encerra.");

var session = new DevSession(config, output, options.Real);
var catalog = new PhoneCatalog(stored ?? new WireflexConfig(), options.Real ? configStore : null, loggerFactory.CreateLogger<PhoneCatalog>());
var adb = new AdbClient(adbPath, loggerFactory.CreateLogger<AdbClient>());

// Os scrcpy ficam amarrados a este processo: se ele morrer (até à força), eles morrem junto.
using var guard = options.Real ? new JobObjectProcessGuard() : null;

ConnectionCoordinator? coordinator = null;
var scrcpyExecutor = options.Real
    ? new ScrcpyActionExecutor(
        new ScrcpyLauncher(scrcpyPath!, adbPath, guard, loggerFactory.CreateLogger<ScrcpyLauncher>()),
        session,
        serial => "Wireflex - " + catalog.DisplayName(serial),
        input => coordinator?.Post(input),
        loggerFactory.CreateLogger<ScrcpyActionExecutor>(),
        windowTitleForLog: serial => "Wireflex - " + catalog.DisplayNameForLog(serial),
        windowCheckDelay: TimeSpan.FromSeconds(1))
    : null;
IActionExecutor executor = scrcpyExecutor ?? (IActionExecutor)new SimulatedActionExecutor();
session.Windows = scrcpyExecutor is null ? null : scrcpyExecutor.Windows;

coordinator = new ConnectionCoordinator(
    new DecisionEngine(serial => config.ResolveConnectionMode(serial)),
    executor,
    adb,
    catalog,
    new ConnectionCoordinatorOptions { PrepareWifi = options.Real, ReconnectWifi = options.Real },
    session,
    loggerFactory);
session.Coordinator = coordinator;

var monitor = new DeviceMonitor(adb, new DeviceMonitorOptions(), loggerFactory.CreateLogger<DeviceMonitor>());

using var shutdown = new CancellationTokenSource();

void RequestShutdown(PosixSignalContext context)
{
    if (shutdown.IsCancellationRequested)
    {
        // Segundo Ctrl+C: deixa o sistema encerrar na hora (o Job Object fecha os scrcpy).
        return;
    }

    context.Cancel = true;
    output.Info("Encerrando...");
    shutdown.Cancel();
}

using var onInterrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);
using var onQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, RequestShutdown);
using var onTerminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);
using var onHangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, RequestShutdown);

var monitorTask = monitor.RunAsync(shutdown.Token);
var coordinatorTask = Task.Run(async () =>
{
    try
    {
        await coordinator.RunAsync(monitor.Events, shutdown.Token);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Sem isto o monitor seguiria rodando sem mostrar nada até o Ctrl+C.
        output.Error($"Falha ao processar eventos: {ex.GetType().Name}: {ex.Message}. Encerrando.");
        shutdown.Cancel();
        throw;
    }
});

// Console.ReadLine bloqueia; fica numa thread de fundo para não segurar o encerramento.
var inputThread = new Thread(() =>
{
    while (!shutdown.IsCancellationRequested)
    {
        string? line;
        try
        {
            line = Console.ReadLine();
        }
        catch (IOException)
        {
            return;
        }

        if (line is null)
        {
            // Sem entrada (ex: stdin fechado): continua monitorando até Ctrl+C.
            return;
        }

        if (!shutdown.IsCancellationRequested && !session.Execute(line))
        {
            output.Info("Encerrando...");
            shutdown.Cancel();
        }
    }
})
{
    IsBackground = true,
    Name = "DevConsole input",
};
inputThread.Start();

try
{
    await Task.WhenAll(monitorTask, coordinatorTask);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    output.Error($"Falha inesperada: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

output.Info("Encerrado.");
return 0;

// Ordem: opção da linha de comando (sem cair para outro se não existir), depois a config, depois o PATH.
static bool TryResolve(
    string name,
    string? fromOption,
    string? fromConfig,
    Func<string?, string?> find,
    out string path,
    out string source)
{
    string? found;
    if (fromOption is not null)
    {
        found = find(fromOption);
        source = $"do --{name}";
    }
    else if (!string.IsNullOrWhiteSpace(fromConfig) && find(fromConfig) is { } configured)
    {
        found = configured;
        source = "da configuração";
    }
    else
    {
        // find(null) procura no PATH.
        found = find(null);
        source = "do PATH";
    }

    path = found ?? string.Empty;
    return found is not null;
}

static WireflexConfig Clone(WireflexConfig config) => new()
{
    SchemaVersion = config.SchemaVersion,
    Global = new GlobalSettings
    {
        ConnectionMode = config.Global.ConnectionMode,
        WifiMode = config.Global.WifiMode,
        StartWithWindows = config.Global.StartWithWindows,
        AdbPath = config.Global.AdbPath,
        ScrcpyPath = config.Global.ScrcpyPath,
    },
    Phones = config.Phones
        .Select(p => new KnownPhone
        {
            Serial = p.Serial,
            FriendlyName = p.FriendlyName,
            Model = p.Model,
            LastKnownIp = p.LastKnownIp,
            Settings = new PhoneSettings { ConnectionMode = p.Settings.ConnectionMode, WifiMode = p.Settings.WifiMode },
        })
        .ToList(),
};
