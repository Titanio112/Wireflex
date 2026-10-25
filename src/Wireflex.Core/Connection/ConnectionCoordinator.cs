using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Decisions;
using Wireflex.Core.Execution;
using Wireflex.Core.Logging;
using Wireflex.Core.Monitoring;

namespace Wireflex.Core.Connection;

/// <summary>Acompanha o que o coordenador faz (o DevConsole mostra na tela). Textos já mascarados.</summary>
public interface IConnectionObserver
{
    void OnDeviceEvent(DeviceEvent deviceEvent);

    void OnAction(EngineAction action);

    void OnNote(string message);
}

public sealed class ConnectionCoordinatorOptions
{
    /// <summary>Ao plugar o cabo: aprende o IP, liga o modo TCP/IP e conecta por Wi-Fi.</summary>
    public bool PrepareWifi { get; init; }

    /// <summary>Tenta conectar por Wi-Fi os celulares conhecidos que estão fora do adb.</summary>
    public bool ReconnectWifi { get; init; }

    public int TcpipPort { get; init; } = WifiPreparer.DefaultTcpipPort;

    /// <summary>Tempo máximo segurando os eventos de um celular durante a preparação do Wi-Fi.</summary>
    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Depois do <c>tcpip</c>, quanto esperar a conexão USB voltar antes de soltar os eventos.</summary>
    public TimeSpan UsbReturnGrace { get; init; } = TimeSpan.FromSeconds(5);

    public int ConnectAttempts { get; init; } = 5;

    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Intervalo entre rodadas de reconexão pelo Wi-Fi; também é a primeira espera de cada celular.</summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Espera máxima entre tentativas de reconexão de um mesmo celular (dobra a cada falha).</summary>
    public TimeSpan MaxReconnectInterval { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Liga monitor, motor e executor, e cuida da troca entre cabo e Wi-Fi sem intervenção.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Tudo passa por uma fila e é processado em ordem, um por vez (o motor não é thread-safe).</item>
/// <item>Cabo plugado sem Wi-Fi conectado: aprende o IP, liga o modo TCP/IP e conecta por Wi-Fi.
/// O <c>tcpip</c> reinicia o adbd e a conexão USB some e volta; por isso os eventos daquele celular ficam
/// retidos até o fim da preparação, e o motor recebe só a diferença (cabo primeiro).</item>
/// <item>Tirar o cabo: o motor troca o scrcpy para o Wi-Fi. Plugar de novo: volta para o cabo.</item>
/// <item>Reconexão: de tempos em tempos, conecta pelo último IP os celulares conhecidos fora do adb.</item>
/// <item>Aprende serial, modelo, nome e IP de cada celular (<see cref="PhoneCatalog"/>).</item>
/// </list>
/// Uso único: chame <see cref="RunAsync"/> uma vez. Ao terminar, fecha os scrcpy abertos.
/// </remarks>
public sealed class ConnectionCoordinator
{
    private readonly DecisionEngine _engine;
    private readonly IActionExecutor _executor;
    private readonly IAdbClient _adb;
    private readonly PhoneCatalog _catalog;
    private readonly WifiPreparer _preparer;
    private readonly ConnectionCoordinatorOptions _options;
    private readonly IConnectionObserver? _observer;
    private readonly ILogger<ConnectionCoordinator> _logger;
    private readonly TimeProvider _time;
    private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _engineSync = new();
    private readonly List<Task> _background = [];

    // Conexões de celulares vistas pelo monitor (por serial do adb) e o que o motor já sabe (por celular + conexão).
    private readonly Dictionary<string, DeviceConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Phone, string Adb), DeviceConnection> _told = [];

    private readonly Dictionary<string, Hold> _holds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _prepared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReconnectState> _reconnect = new(StringComparer.Ordinal);
    private bool _adbAvailable;
    private int _holdGeneration;
    private int _started;
    private CancellationToken _stopToken;

    public ConnectionCoordinator(
        DecisionEngine engine,
        IActionExecutor executor,
        IAdbClient adb,
        PhoneCatalog catalog,
        ConnectionCoordinatorOptions? options = null,
        IConnectionObserver? observer = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? new ConnectionCoordinatorOptions();
        _observer = observer;
        loggerFactory ??= NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger<ConnectionCoordinator>();
        _time = timeProvider ?? TimeProvider.System;
        _preparer = new WifiPreparer(
            adb,
            loggerFactory.CreateLogger<WifiPreparer>(),
            _time,
            _options.TcpipPort,
            _options.ConnectAttempts,
            _options.ConnectRetryDelay);
    }

    /// <summary>Entrada vinda de fora do monitor: executor (scrcpy abriu/fechou), usuário (respondeu, pausou).</summary>
    public void Post(EngineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _inbox.Writer.TryWrite(new InputMessage(input));
    }

    /// <summary>Evento do monitor, pelo mesmo caminho que <see cref="RunAsync"/> usa para o canal do monitor.</summary>
    public void PostDeviceEvent(DeviceEvent deviceEvent)
    {
        ArgumentNullException.ThrowIfNull(deviceEvent);
        _inbox.Writer.TryWrite(new MonitorMessage(deviceEvent));
    }

    /// <summary>Estado de cada celular no motor.</summary>
    public IReadOnlyList<PhoneStatus> GetStatus()
    {
        lock (_engineSync)
        {
            return _engine.GetStatus();
        }
    }

    /// <summary>Completa quando tudo o que já estava na fila foi processado. Para testes e ferramentas.</summary>
    public Task WhenIdleAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inbox.Writer.TryWrite(new BarrierMessage(done)))
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    public async Task RunAsync(ChannelReader<DeviceEvent> monitorEvents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitorEvents);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("O coordenador já foi iniciado.");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stopToken = stop.Token;
        var pump = PumpAsync(monitorEvents, stop.Token);
        var ticker = _options.ReconnectWifi ? TickAsync(stop.Token) : Task.CompletedTask;

        try
        {
            await foreach (var message in _inbox.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                await HandleAsync(message, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            _inbox.Writer.TryComplete();
            await WaitQuietlyAsync([pump, ticker, .. _background]).ConfigureAwait(false);

            // Fecha os scrcpy abertos: nada fica rodando depois do Wireflex.
            await _executor.DisposeAsync().ConfigureAwait(false);
            while (_inbox.Reader.TryRead(out var leftover))
            {
                if (leftover is BarrierMessage barrier)
                {
                    barrier.Done.TrySetResult();
                }
            }
        }
    }

    private async Task PumpAsync(ChannelReader<DeviceEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var deviceEvent in events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _inbox.Writer.TryWrite(new MonitorMessage(deviceEvent));
            }
        }
        finally
        {
            // Monitor parou: termina de processar a fila e encerra.
            _inbox.Writer.TryComplete();
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(_options.ReconnectInterval, _time, cancellationToken).ConfigureAwait(false);
            _inbox.Writer.TryWrite(new ReconnectTickMessage());
        }
    }

    private async Task HandleAsync(Message message, CancellationToken cancellationToken)
    {
        switch (message)
        {
            case MonitorMessage m:
                await OnMonitorEventAsync(m.Event, cancellationToken).ConfigureAwait(false);
                break;
            case InputMessage i:
                await ApplyAsync(i.Input, cancellationToken).ConfigureAwait(false);
                break;
            case PreparedMessage p:
                await OnPreparedAsync(p, cancellationToken).ConfigureAwait(false);
                break;
            case ReleaseHoldMessage r:
                if (_holds.TryGetValue(r.Serial, out var hold) && hold.Generation == r.Generation)
                {
                    Note($"O cabo de {Sensitive.Mask(r.Serial)} não voltou; seguindo com o que há");
                    await ReleaseAsync(r.Serial, cancellationToken).ConfigureAwait(false);
                }

                break;
            case ReconnectTickMessage:
                StartReconnects();
                break;
            case ReconnectedMessage r:
                OnReconnected(r);
                break;
            case BarrierMessage b:
                b.Done.TrySetResult();
                break;
        }

        _background.RemoveAll(t => t.IsCompleted);
    }

    private async Task OnMonitorEventAsync(DeviceEvent deviceEvent, CancellationToken cancellationToken)
    {
        _observer?.OnDeviceEvent(deviceEvent);

        DeviceConnection connection;
        switch (deviceEvent)
        {
            case MonitorStatusChanged status:
                _adbAvailable = status.AdbAvailable;
                if (status.AdbAvailable)
                {
                    await ApplyAsync(new Reevaluate(null, "servidor do adb disponível"), cancellationToken).ConfigureAwait(false);
                }

                return;
            case DeviceAppeared { Connection: var c } when MonitorEventTranslator.IsPhone(c):
                _connections[c.AdbSerial] = c;
                connection = c;
                break;
            case DeviceStateChanged { Connection: var c } when MonitorEventTranslator.IsPhone(c):
                _connections[c.AdbSerial] = c;
                connection = c;
                break;
            case DeviceDisappeared { Connection: var c } when MonitorEventTranslator.IsPhone(c):
                if (_connections.TryGetValue(c.AdbSerial, out var current) && current.Key == c.Key)
                {
                    _connections.Remove(c.AdbSerial);
                }

                connection = c;
                break;
            default:
                return;
        }

        await OnPhoneChangedAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnPhoneChangedAsync(DeviceConnection connection, CancellationToken cancellationToken)
    {
        var serial = connection.Key;

        if (_connections.ContainsKey(connection.AdbSerial) && connection.State == AdbDeviceState.Device)
        {
            _reconnect.Remove(serial);
            var ip = connection.Transport == AdbTransport.WifiTcpip
                && AdbSerialClassifier.TryParseHostPort(connection.AdbSerial, out var host, out _)
                ? host
                : null;
            await _catalog.LearnAsync(serial, model: connection.Model, ip: ip, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (_holds.TryGetValue(serial, out var hold))
        {
            // Segurando: o motor só fica sabendo quando a preparação terminar (e o cabo voltar).
            if (hold.Prepared && UsbReady(serial))
            {
                await ReleaseAsync(serial, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        MaybeStartPreparation(serial);
        if (_holds.ContainsKey(serial))
        {
            return;
        }

        if (!HasUsb(serial))
        {
            _prepared.Remove(serial);
        }

        await SyncAsync(serial, cancellationToken).ConfigureAwait(false);
    }

    private void MaybeStartPreparation(string serial)
    {
        if (!_options.PrepareWifi || !_adbAvailable || _prepared.Contains(serial))
        {
            return;
        }

        var usb = _connections.Values.FirstOrDefault(c =>
            c.Key == serial && c.Transport == AdbTransport.Usb && c.State == AdbDeviceState.Device);
        if (usb is null)
        {
            return;
        }

        _prepared.Add(serial);
        var wifiReady = _connections.Values.Any(c =>
            c.Key == serial && c.Transport != AdbTransport.Usb && c.State == AdbDeviceState.Device);
        var connectWifi = !wifiReady && _catalog.ResolveWifiMode(serial) != WifiMode.WirelessDebugging;

        if (connectWifi)
        {
            _holds[serial] = new Hold(++_holdGeneration);
            Note($"Cabo plugado em {Sensitive.Mask(serial)}: preparando o Wi-Fi (o espelhamento abre em seguida)");
        }

        _background.Add(PrepareAsync(serial, usb.AdbSerial, connectWifi));
    }

    private async Task PrepareAsync(string serial, string usbAdbSerial, bool connectWifi)
    {
        WifiPreparation? result = null;
        using var timeout = new CancellationTokenSource(_options.PreparationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, timeout.Token);
        try
        {
            result = await _preparer.PrepareAsync(usbAdbSerial, connectWifi, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_stopToken.IsCancellationRequested)
        {
            _logger.LogWarning("A preparação do Wi-Fi de {Phone} passou do tempo", Sensitive.Mask(serial));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha ao preparar o Wi-Fi de {Phone}", Sensitive.Mask(serial));
        }

        _inbox.Writer.TryWrite(new PreparedMessage(serial, result));
    }

    private async Task OnPreparedAsync(PreparedMessage message, CancellationToken cancellationToken)
    {
        var serial = message.Serial;
        if (message.Result is { } result)
        {
            await _catalog.LearnAsync(serial, friendlyName: result.DeviceName, ip: result.Ip, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Note(Describe(serial, result));
        }
        else if (_holds.ContainsKey(serial))
        {
            Note($"Não deu para preparar o Wi-Fi de {Sensitive.Mask(serial)}; seguindo pelo cabo");
        }

        if (!_holds.TryGetValue(serial, out var hold))
        {
            return;
        }

        hold.Prepared = true;
        if (UsbReady(serial) || message.Result is not { TcpipEnabled: true })
        {
            await ReleaseAsync(serial, cancellationToken).ConfigureAwait(false);
            return;
        }

        // O tcpip derrubou a conexão USB; espera um pouco ela voltar para o espelhamento abrir pelo cabo.
        _background.Add(ReleaseLaterAsync(serial, hold.Generation));
    }

    private async Task ReleaseLaterAsync(string serial, int generation)
    {
        try
        {
            await Task.Delay(_options.UsbReturnGrace, _time, _stopToken).ConfigureAwait(false);
            _inbox.Writer.TryWrite(new ReleaseHoldMessage(serial, generation));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReleaseAsync(string serial, CancellationToken cancellationToken)
    {
        _holds.Remove(serial);
        if (!HasUsb(serial))
        {
            _prepared.Remove(serial);
        }

        await SyncAsync(serial, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Conta ao motor a diferença entre o que ele sabe e o que o monitor vê para este celular.
    /// Aparecimentos primeiro (cabo antes do Wi-Fi), depois mudanças de estado e por último sumiços.
    /// </summary>
    private async Task SyncAsync(string serial, CancellationToken cancellationToken)
    {
        var now = _connections.Values
            .Where(c => c.Key == serial)
            .OrderBy(c => AdbTransports.Priority(c.Transport))
            .ThenBy(c => c.AdbSerial, StringComparer.Ordinal)
            .ToList();
        var before = _told.Where(p => p.Key.Phone == serial).Select(p => p.Value).ToList();

        var inputs = new List<EngineInput>();
        foreach (var c in now)
        {
            if (!_told.TryGetValue((serial, c.AdbSerial), out var told))
            {
                inputs.Add(new ConnectionAdded(serial, c.AdbSerial, c.Transport, c.State));
            }
            else if (told.State != c.State)
            {
                inputs.Add(new ConnectionStateChanged(serial, c.AdbSerial, c.State));
            }

            _told[(serial, c.AdbSerial)] = c;
        }

        // Os aparecimentos vêm antes das mudanças de estado (ex: Wi-Fi novo antes do cabo ficar offline).
        // OrderBy é estável: mantém o cabo antes do Wi-Fi.
        inputs = inputs.OrderBy(Rank).ToList();

        foreach (var c in before.Where(c => now.All(n => n.AdbSerial != c.AdbSerial)))
        {
            inputs.Add(new ConnectionRemoved(serial, c.AdbSerial));
            _told.Remove((serial, c.AdbSerial));
        }

        foreach (var input in inputs)
        {
            await ApplyAsync(input, cancellationToken).ConfigureAwait(false);
        }

        static int Rank(EngineInput input) => input is ConnectionAdded ? 0 : 1;
    }

    private async Task ApplyAsync(EngineInput input, CancellationToken cancellationToken)
    {
        IReadOnlyList<EngineAction> actions;
        lock (_engineSync)
        {
            actions = _engine.Handle(input);
        }

        foreach (var action in actions)
        {
            _observer?.OnAction(action);
            if (action is DoNothing)
            {
                continue;
            }

            try
            {
                await _executor.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha ao executar {Action}", action.GetType().Name);
            }
        }
    }

    private void StartReconnects()
    {
        if (!_adbAvailable)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var phone in _catalog.Phones)
        {
            if (string.IsNullOrWhiteSpace(phone.LastKnownIp)
                || _catalog.ResolveWifiMode(phone.Serial) == WifiMode.WirelessDebugging
                || _connections.Values.Any(c => c.Key == phone.Serial))
            {
                continue;
            }

            if (!_reconnect.TryGetValue(phone.Serial, out var state))
            {
                state = new ReconnectState(now, _options.ReconnectInterval);
                _reconnect[phone.Serial] = state;
            }

            if (state.InFlight || now < state.NextAttempt)
            {
                continue;
            }

            state.InFlight = true;
            _background.Add(ReconnectAsync(phone.Serial, phone.LastKnownIp));
        }
    }

    private async Task ReconnectAsync(string serial, string ip)
    {
        bool connected;
        string? detail;
        try
        {
            var result = await _adb.ConnectAsync(ip, _options.TcpipPort, _stopToken).ConfigureAwait(false);
            connected = result.Connected;
            detail = result.Message;
        }
        catch (Exception ex) when (ex is AdbException or IOException)
        {
            connected = false;
            detail = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _inbox.Writer.TryWrite(new ReconnectedMessage(serial, connected, detail));
    }

    private void OnReconnected(ReconnectedMessage message)
    {
        if (!_reconnect.TryGetValue(message.Serial, out var state))
        {
            return;
        }

        state.InFlight = false;
        if (message.Connected)
        {
            Note($"Reconectado pelo Wi-Fi: {Sensitive.Mask(message.Serial)}");
            state.Delay = _options.ReconnectInterval;
        }
        else
        {
            _logger.LogDebug("Reconexão de {Phone} falhou: {Detail}", Sensitive.Mask(message.Serial), Sensitive.Scrub(message.Detail ?? string.Empty));
            state.Delay = TimeSpan.FromTicks(Math.Min(state.Delay.Ticks * 2, _options.MaxReconnectInterval.Ticks));
        }

        state.NextAttempt = _time.GetUtcNow() + state.Delay;
    }

    private bool HasUsb(string serial) =>
        _connections.Values.Any(c => c.Key == serial && c.Transport == AdbTransport.Usb);

    private bool UsbReady(string serial) =>
        _connections.Values.Any(c => c.Key == serial && c.Transport == AdbTransport.Usb && c.State == AdbDeviceState.Device);

    private string Describe(string serial, WifiPreparation result)
    {
        var phone = Sensitive.Mask(serial);
        var address = result.Ip is null
            ? "IP desconhecido"
            : Sensitive.Mask(result.Ip) + ":" + _options.TcpipPort.ToString(CultureInfo.InvariantCulture);
        return result.Outcome switch
        {
            WifiPreparationOutcome.Connected => $"Wi-Fi de {phone} pronto em {address}{(result.TcpipEnabled ? " (modo TCP/IP ligado agora)" : string.Empty)}",
            WifiPreparationOutcome.NoWifi => $"{phone} não está em nenhuma rede Wi-Fi; só pelo cabo",
            WifiPreparationOutcome.ConnectFailed => $"Não conectou {phone} pelo Wi-Fi ({address}): {Sensitive.Scrub(result.Detail ?? "sem resposta")}",
            _ => $"Aprendido {phone}: {address}",
        };
    }

    private void Note(string message)
    {
        _logger.LogInformation("{Note}", message);
        _observer?.OnNote(message);
    }

    private static async Task WaitQuietlyAsync(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class Hold(int generation)
    {
        public int Generation { get; } = generation;

        public bool Prepared { get; set; }
    }

    private sealed class ReconnectState(DateTimeOffset nextAttempt, TimeSpan delay)
    {
        public DateTimeOffset NextAttempt { get; set; } = nextAttempt;

        public TimeSpan Delay { get; set; } = delay;

        public bool InFlight { get; set; }
    }

    private abstract record Message;

    private sealed record MonitorMessage(DeviceEvent Event) : Message;

    private sealed record InputMessage(EngineInput Input) : Message;

    private sealed record PreparedMessage(string Serial, WifiPreparation? Result) : Message;

    private sealed record ReleaseHoldMessage(string Serial, int Generation) : Message;

    private sealed record ReconnectTickMessage : Message;

    private sealed record ReconnectedMessage(string Serial, bool Connected, string? Detail) : Message;

    private sealed record BarrierMessage(TaskCompletionSource Done) : Message;
}
