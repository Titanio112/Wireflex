using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Wireflex.Core;
using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Hosting;
using Wireflex.Core.Logging;
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

if (ToolPaths.Resolve(options.AdbPath, config.Global.AdbPath, AdbLocator.Find) is not { } adb)
{
    output.Error(options.AdbPath is not null
        ? $"adb não encontrado em: {options.AdbPath}"
        : "adb não encontrado na configuração nem no PATH. Use --adb <caminho do adb.exe>.");
    return 1;
}

output.Info($"adb ({Source("adb", adb.Source)}): {adb.Path}");

var scrcpy = ToolPaths.Resolve(options.ScrcpyPath, config.Global.ScrcpyPath, ScrcpyLocator.Find);
if (scrcpy is not null)
{
    output.Info($"scrcpy ({Source("scrcpy", scrcpy.Source)}): {scrcpy.Path}");
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

// Os scrcpy ficam amarrados a este processo: se ele morrer (até à força), eles morrem junto.
using var guard = options.Real ? new JobObjectProcessGuard() : null;

var runtime = new WireflexRuntime(new WireflexRuntimeSettings
{
    AdbPath = adb.Path,
    ScrcpyPath = scrcpy?.Path,
    Real = options.Real,
    Catalog = catalog,
    Notifier = session,
    Observer = session,

    // A cópia da sessão: --modo e o comando m valem só aqui.
    ResolveConnectionMode = serial => config.ResolveConnectionMode(serial),
    Guard = guard,
    LoggerFactory = loggerFactory,
});
session.Windows = runtime.Scrcpy is { } scrcpyExecutor ? scrcpyExecutor.Windows : null;
session.Coordinator = runtime.Coordinator;

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

var runTask = runtime.RunAsync(shutdown.Token);

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
    await runTask;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // O monitor já parou junto com o coordenador.
    output.Error($"Falha ao processar eventos: {ex.GetType().Name}: {ex.Message}. Encerrado.");
    return 1;
}

output.Info("Encerrado.");
return 0;

static string Source(string name, ToolSource source) => source switch
{
    ToolSource.Option => $"do --{name}",
    ToolSource.Config => "da configuração",
    _ => "do PATH",
};

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
