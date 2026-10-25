using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Wireflex.Core;
using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Decisions;
using Wireflex.Core.Logging;
using Wireflex.Core.Monitoring;
using Wireflex.DevConsole;

// Ferramenta de teste manual: monitor real do adb + motor de decisão, imprimindo eventos e ações.
// Só lê: não abre scrcpy, não roda adb tcpip/connect e não grava o config.json.

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

// Configuração só em memória: lê o config.json real, se existir, sem nunca gravar nele.
var configStore = new JsonConfigStore(WireflexPaths.ConfigDirectory, loggerFactory.CreateLogger<JsonConfigStore>());
var storedConfig = await configStore.TryReadAsync();
var config = storedConfig ?? new WireflexConfig();
if (options.ConnectionMode is { } mode)
{
    config.Global.ConnectionMode = mode;
}

var adbPath = options.AdbPath is not null
    ? AdbLocator.Find(options.AdbPath)
    : AdbLocator.Find(config.Global.AdbPath) ?? AdbLocator.Find(null);
if (adbPath is null)
{
    output.Error(options.AdbPath is not null
        ? $"adb não encontrado em: {options.AdbPath}"
        : "adb não encontrado na configuração nem no PATH. Use --adb <caminho do adb.exe>.");
    return 1;
}

output.Info($"adb: {adbPath}");
output.Info(storedConfig is null
    ? "Configuração: padrão, só em memória (nada é gravado)"
    : "Configuração: lida de %APPDATA%\\Wireflex\\config.json, só em memória (nada é gravado)");
output.Info($"Modo de conexão global: {Format.Mode(config.Global.ConnectionMode)}");
output.Info("Logs: %LOCALAPPDATA%\\Wireflex\\logs (serial e IP mascarados)");
output.Info("Monitor ligado. Digite h para ver os comandos. Ctrl+C encerra.");

var engine = new DecisionEngine(serial => config.ResolveConnectionMode(serial));
var session = new DevSession(engine, config, output);
var monitor = new DeviceMonitor(
    new AdbClient(adbPath, loggerFactory.CreateLogger<AdbClient>()),
    new DeviceMonitorOptions(),
    loggerFactory.CreateLogger<DeviceMonitor>());

using var shutdown = new CancellationTokenSource();

void RequestShutdown(PosixSignalContext context)
{
    if (shutdown.IsCancellationRequested)
    {
        // Segundo Ctrl+C: deixa o sistema encerrar na hora.
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
var eventsTask = Task.Run(async () =>
{
    try
    {
        await foreach (var deviceEvent in monitor.Events.ReadAllAsync())
        {
            session.OnDeviceEvent(deviceEvent);
        }
    }
    catch (Exception ex)
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
    await Task.WhenAll(monitorTask, eventsTask);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    output.Error($"Falha inesperada: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

output.Info("Encerrado.");
return 0;
