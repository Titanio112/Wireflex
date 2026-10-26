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
using Wireflex.Core.Scrcpy;

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

    /// <summary>Ao esquecer um celular, roda <c>adb disconnect</c> do Wi-Fi dele (só no modo real).</summary>
    public bool DisconnectWifiOnForget { get; init; }

    public int TcpipPort { get; init; } = WifiPreparer.DefaultTcpipPort;

    /// <summary>
    /// Tempo máximo segurando o espelhamento depois do cabo (só quando o <c>tcpip</c> é necessário).
    /// Estourou: solta e o motor abre pelo que houver.
    /// </summary>
    public TimeSpan HoldLimit { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Depois do <c>tcpip</c>, se a conexão USB não cair neste tempo (o adbd reiniciou rápido demais para o
    /// monitor ver), ela é considerada estável e o espelhamento abre.
    /// </summary>
    public TimeSpan UsbStableWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Limite de segurança da preparação inteira, em segundo plano (não segura o espelhamento).</summary>
    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Passos do connect: espera depois do tcpip, limite por tentativa e total.</summary>
    public WifiPreparerOptions Preparer { get; init; } = new();

    /// <summary>Intervalo entre rodadas de reconexão pelo Wi-Fi; também é a primeira espera de cada celular.</summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Espera máxima entre tentativas de reconexão de um mesmo celular (dobra a cada falha).</summary>
    public TimeSpan MaxReconnectInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Espera antes de abrir de novo um scrcpy que falhou logo ao abrir (ex: Wi-Fi instável logo depois da troca).</summary>
    public TimeSpan ScrcpyRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Máximo de novas tentativas seguidas por celular, até a conexão mudar ou um scrcpy ficar aberto.</summary>
    public int ScrcpyRetryLimit { get; init; } = 3;

    /// <summary>
    /// Quanto tempo o celular precisa ficar offline para o aviso sair. Logo depois de tirar o cabo o Wi-Fi costuma
    /// ficar offline ~1 s e voltar sozinho: isso não merece aviso.
    /// </summary>
    public TimeSpan OfflineNoticeDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Liga monitor, motor e executor, e cuida da troca entre cabo e Wi-Fi sem intervenção.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Tudo passa por uma fila e é processado em ordem, um por vez (o motor não é thread-safe).</item>
/// <item>Cabo plugado sem Wi-Fi conectado: lê IP, nome e a porta do TCP/IP pelo cabo (~0,1 s), com os eventos
/// daquele celular retidos.
/// <list type="bullet">
/// <item>TCP/IP já ligado: solta na hora (espelha pelo cabo) e conecta o Wi-Fi em segundo plano.</item>
/// <item>TCP/IP desligado: roda <c>tcpip</c>, que reinicia o adbd (a conexão USB some e volta). Solta quando o USB
/// voltar, ou se ele não cair em <see cref="ConnectionCoordinatorOptions.UsbStableWindow"/>, ou no limite
/// <see cref="ConnectionCoordinatorOptions.HoldLimit"/>; o connect segue em segundo plano.</item>
/// </list>
/// Ao soltar, o motor recebe só a diferença (cabo primeiro).</item>
/// <item>Tirar o cabo: o motor troca o scrcpy para o Wi-Fi. Plugar de novo: volta para o cabo.</item>
/// <item>Reconexão: de tempos em tempos, conecta pelo último IP os celulares conhecidos sem conexão Wi-Fi
/// (fora do alcance, ou no cabo depois de o servidor do adb reiniciar).</item>
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

    // Preparação em andamento (inclusive o connect em segundo plano): a reconexão não tenta junto.
    private readonly HashSet<string> _preparing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReconnectState> _reconnect = new(StringComparer.Ordinal);

    // Novas tentativas de abrir o scrcpy depois de ele falhar ao iniciar.
    private readonly Dictionary<string, ScrcpyRetryState> _scrcpyRetries = new(StringComparer.Ordinal);
    private int _scrcpyRetryGeneration;

    // Esquecidos ainda ao alcance: não aprende, não prepara nem reconecta até saírem do alcance.
    private readonly HashSet<string> _forgotten = new(StringComparer.Ordinal);

    // Aviso de offline esperando para ver se o offline dura (geração por celular).
    private readonly Dictionary<string, int> _pendingOfflineNotices = new(StringComparer.Ordinal);
    private int _offlineNoticeGeneration;
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
            new WifiPreparerOptions
            {
                Port = _options.TcpipPort,
                TcpipSettleDelay = _options.Preparer.TcpipSettleDelay,
                ConnectAttemptTimeout = _options.Preparer.ConnectAttemptTimeout,
                ConnectLimit = _options.Preparer.ConnectLimit,
                ConnectRetryDelay = _options.Preparer.ConnectRetryDelay,
            });
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

    /// <summary>
    /// Esquece o celular: tira da config e desconecta o Wi-Fi dele (<c>adb disconnect</c>). Se ele ainda estiver
    /// ao alcance, o scrcpy fecha e ele fica ignorado (sem abrir, aprender nem reconectar) até sair do alcance e voltar.
    /// Completa quando a fila processou o pedido.
    /// </summary>
    public Task ForgetAsync(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inbox.Writer.TryWrite(new ForgetMessage(serial, done)))
        {
            done.TrySetResult();
        }

        return done.Task;
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
                OnInputApplied(i.Input);
                break;
            case ScrcpyRetryMessage r:
                await OnScrcpyRetryAsync(r, cancellationToken).ConfigureAwait(false);
                break;
            case OfflineNoticeMessage o:
                await OnOfflineNoticeDueAsync(o, cancellationToken).ConfigureAwait(false);
                break;
            case ForgetMessage f:
                try
                {
                    await OnForgetAsync(f.Serial, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    f.Done.TrySetResult();
                }

                break;
            case LearnedMessage l when !_forgotten.Contains(l.Serial):
                // Grava já: se o tcpip/connect demorar ou falhar, o IP e o nome não se perdem.
                await _catalog.LearnAsync(l.Serial, friendlyName: l.DeviceName, ip: l.Ip, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ReadyMessage r:
                await OnReadyAsync(r, cancellationToken).ConfigureAwait(false);
                break;
            case PreparedMessage p:
                await OnPreparedAsync(p, cancellationToken).ConfigureAwait(false);
                break;
            case ReleaseHoldMessage r:
                await OnReleaseTimerAsync(r, cancellationToken).ConfigureAwait(false);
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
                if (!status.AdbAvailable)
                {
                    // Servidor caiu: o que se sabia das tentativas de reconexão ficou velho.
                    _reconnect.Clear();
                    return;
                }

                await ApplyAsync(new Reevaluate(null, "servidor do adb disponível"), cancellationToken).ConfigureAwait(false);

                // Servidor novo (ou que acabou de subir) não lembra das conexões Wi-Fi: tenta já, sem esperar a rodada.
                if (_options.ReconnectWifi)
                {
                    StartReconnects();
                }

                return;
            case DeviceAppeared { Connection: var c } when MonitorEventTranslator.IsPhone(c):
                // Eventos de aparelho só chegam com o adb no ar. Na partida eles vêm antes do aviso
                // "adb disponível", e sem isto quem já estava no cabo não seria preparado.
                _adbAvailable = true;
                _connections[c.AdbSerial] = c;
                connection = c;
                break;
            case DeviceStateChanged { Connection: var c } when MonitorEventTranslator.IsPhone(c):
                _adbAvailable = true;
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

        // A conexão mudou: o motor reavalia por conta própria e as novas tentativas recomeçam do zero.
        ResetScrcpyRetries(serial);

        // Esquecido saiu do alcance (sem cabo e sem Wi-Fi): da próxima vez que aparecer, é um celular novo.
        if (!InRange(serial))
        {
            _forgotten.Remove(serial);
        }

        if (_connections.ContainsKey(connection.AdbSerial) && connection.State == AdbDeviceState.Device
            && !_forgotten.Contains(serial))
        {
            if (connection.Transport != AdbTransport.Usb)
            {
                _reconnect.Remove(serial);
            }

            var ip = connection.Transport == AdbTransport.WifiTcpip
                && AdbSerialClassifier.TryParseHostPort(connection.AdbSerial, out var host, out _)
                ? host
                : null;
            await _catalog.LearnAsync(serial, model: connection.Model, ip: ip, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (_holds.TryGetValue(serial, out var hold))
        {
            if (connection.Transport == AdbTransport.Usb)
            {
                var present = _connections.ContainsKey(connection.AdbSerial);
                _logger.LogInformation(
                    "Tempo {Phone}: USB {State} {Seconds:0.000} s depois do cabo",
                    Sensitive.Mask(serial),
                    present ? connection.State.ToString() : "sumiu",
                    _time.GetElapsedTime(hold.StartedTimestamp).TotalSeconds);
                if (!present || connection.State != AdbDeviceState.Device)
                {
                    hold.SawUsbDrop = true;
                }
            }

            // Segurando: depois do tcpip, solta quando a conexão USB cair e voltar.
            if (hold.TcpipDone && hold.SawUsbDrop && UsbReady(serial))
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
        if (!_options.PrepareWifi || !_adbAvailable || _prepared.Contains(serial) || _forgotten.Contains(serial))
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
            // Segura só o tempo de ler a porta do TCP/IP (~0,1 s); se o tcpip for necessário, até o USB voltar.
            var hold = new Hold(++_holdGeneration, _time.GetTimestamp());
            _holds[serial] = hold;
            _background.Add(ReleaseLaterAsync(serial, hold.Generation, _options.HoldLimit, ReleaseReason.Limit));
        }

        _preparing.Add(serial);
        _background.Add(PrepareAsync(serial, usb.AdbSerial, connectWifi));
    }

    /// <summary>
    /// Em segundo plano: lê pelo cabo, liga o tcpip se precisar e conecta. Avisa o laço em cada etapa
    /// (<see cref="LearnedMessage"/>, <see cref="ReadyMessage"/>, <see cref="PreparedMessage"/>), que decide quando soltar.
    /// </summary>
    private async Task PrepareAsync(string serial, string usbAdbSerial, bool connectWifi)
    {
        WifiPreparation? result = null;
        using var timeout = new CancellationTokenSource(_options.PreparationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, timeout.Token);
        try
        {
            var probe = await _preparer.ProbeAsync(usbAdbSerial, linked.Token).ConfigureAwait(false);
            _inbox.Writer.TryWrite(new LearnedMessage(serial, probe.Ip, probe.DeviceName));

            if (!connectWifi)
            {
                result = new WifiPreparation(probe.Ip, probe.DeviceName, WifiPreparationOutcome.LearnedOnly, TcpipEnabled: false);
            }
            else if (_preparer.NoWifiOutcome(usbAdbSerial, probe) is { } noWifi)
            {
                result = noWifi;
            }
            else
            {
                var tcpip = !probe.TcpipActive;
                if (tcpip && !await _preparer.EnableTcpipAsync(usbAdbSerial, linked.Token).ConfigureAwait(false))
                {
                    result = new WifiPreparation(probe.Ip, probe.DeviceName, WifiPreparationOutcome.ConnectFailed, false, "tcpip falhou");
                }
                else
                {
                    _inbox.Writer.TryWrite(new ReadyMessage(serial, tcpip));
                    result = await _preparer.ConnectAsync(usbAdbSerial, probe.Ip!, probe.DeviceName, tcpip, linked.Token)
                        .ConfigureAwait(false);
                }
            }
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

    /// <summary>Leu a porta do TCP/IP (e rodou o tcpip, se precisava). O connect segue em segundo plano.</summary>
    private async Task OnReadyAsync(ReadyMessage message, CancellationToken cancellationToken)
    {
        var serial = message.Serial;
        if (!_holds.TryGetValue(serial, out var hold))
        {
            return;
        }

        if (!message.TcpipRestarted)
        {
            Note($"Cabo em {Sensitive.Mask(serial)}: TCP/IP já ligado; espelhando pelo cabo e conectando o Wi-Fi em segundo plano");
            await ReleaseAsync(serial, cancellationToken).ConfigureAwait(false);
            return;
        }

        Note($"Cabo em {Sensitive.Mask(serial)}: ligando o TCP/IP (o adb do celular reinicia; o espelhamento abre quando o cabo voltar)");
        hold.TcpipDone = true;
        if (hold.SawUsbDrop && UsbReady(serial))
        {
            await ReleaseAsync(serial, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Se o USB não cair logo, o adbd reiniciou rápido demais para o monitor ver: considera estável.
        _background.Add(ReleaseLaterAsync(serial, hold.Generation, _options.UsbStableWindow, ReleaseReason.UsbStable));
    }

    private async Task OnReleaseTimerAsync(ReleaseHoldMessage message, CancellationToken cancellationToken)
    {
        if (!_holds.TryGetValue(message.Serial, out var hold) || hold.Generation != message.Generation)
        {
            return;
        }

        switch (message.Reason)
        {
            case ReleaseReason.UsbStable when !hold.SawUsbDrop && UsbReady(message.Serial):
                await ReleaseAsync(message.Serial, cancellationToken).ConfigureAwait(false);
                break;
            case ReleaseReason.Limit:
                Note($"{Sensitive.Mask(message.Serial)} não ficou pronto em {_options.HoldLimit.TotalSeconds:0} s; abrindo com o que há");
                await ReleaseAsync(message.Serial, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task OnPreparedAsync(PreparedMessage message, CancellationToken cancellationToken)
    {
        var serial = message.Serial;
        _preparing.Remove(serial);
        if (message.Result is { Outcome: WifiPreparationOutcome.Connected })
        {
            // Acabou de conectar: o Wi-Fi ainda pode não ter aparecido no monitor. A reconexão espera a rodada.
            _reconnect[serial] = new ReconnectState(_time.GetUtcNow() + _options.ReconnectInterval, _options.ReconnectInterval);
        }
        if (message.Result is { } result && !_forgotten.Contains(serial))
        {
            await _catalog.LearnAsync(serial, friendlyName: result.DeviceName, ip: result.Ip, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Note(Describe(serial, result));
        }
        else if (_holds.ContainsKey(serial))
        {
            Note($"Não deu para preparar o Wi-Fi de {Sensitive.Mask(serial)}; seguindo pelo cabo");
        }

        // Ainda segurando (sem Wi-Fi, não respondeu, tcpip falhou): não há o que esperar.
        if (_holds.ContainsKey(serial))
        {
            await ReleaseAsync(serial, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReleaseLaterAsync(string serial, int generation, TimeSpan delay, ReleaseReason reason)
    {
        try
        {
            await Task.Delay(delay, _time, _stopToken).ConfigureAwait(false);
            _inbox.Writer.TryWrite(new ReleaseHoldMessage(serial, generation, reason));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Depois de o motor ver a entrada: o motor não reabre um scrcpy que encerrou sozinho (evita laço) e espera o
    /// estado do adb mudar. Se ele falhou logo ao abrir e nada mudar, o celular ficaria sem espelho: tenta de novo,
    /// no máximo <see cref="ConnectionCoordinatorOptions.ScrcpyRetryLimit"/> vezes seguidas.
    /// </summary>
    private void OnInputApplied(EngineInput input)
    {
        switch (input)
        {
            case ScrcpyExited { FailedAtStart: true } e:
                ScheduleScrcpyRetry(e.Serial);
                break;
            case ScrcpyExited e when e.Runtime > ScrcpyExit.StartupWindow:
                // Ficou aberto um tempo: a sequência de falhas acabou.
                ResetScrcpyRetries(e.Serial);
                break;
            case ScrcpyClosedByUser e:
                ResetScrcpyRetries(e.Serial);
                break;
            case PhonePauseChanged e:
                ResetScrcpyRetries(e.Serial);
                break;
        }
    }

    private void ScheduleScrcpyRetry(string serial)
    {
        if (!_scrcpyRetries.TryGetValue(serial, out var state))
        {
            state = new ScrcpyRetryState();
            _scrcpyRetries[serial] = state;
        }

        if (state.Attempts >= _options.ScrcpyRetryLimit)
        {
            Note($"O scrcpy de {Sensitive.Mask(serial)} falhou ao abrir {state.Attempts + 1} vezes seguidas; só tenta de novo quando a conexão mudar");
            return;
        }

        state.Attempts++;
        state.Generation = ++_scrcpyRetryGeneration;
        _background.Add(ScrcpyRetryLaterAsync(serial, state.Generation));
    }

    private async Task ScrcpyRetryLaterAsync(string serial, int generation)
    {
        try
        {
            await Task.Delay(_options.ScrcpyRetryDelay, _time, _stopToken).ConfigureAwait(false);
            _inbox.Writer.TryWrite(new ScrcpyRetryMessage(serial, generation));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task OnScrcpyRetryAsync(ScrcpyRetryMessage message, CancellationToken cancellationToken)
    {
        if (!_scrcpyRetries.TryGetValue(message.Serial, out var state) || state.Generation != message.Generation)
        {
            return;
        }

        // O motor decide: não abre se o usuário fechou, se está pausado, fora do alcance ou já espelhando.
        Note($"Tentando abrir o scrcpy de {Sensitive.Mask(message.Serial)} de novo ({state.Attempts} de {_options.ScrcpyRetryLimit})");
        await ApplyAsync(new Reevaluate(message.Serial, "nova tentativa depois de o scrcpy falhar ao abrir"), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Zera as novas tentativas; uma tentativa já agendada é descartada.</summary>
    private void ResetScrcpyRetries(string serial) => _scrcpyRetries.Remove(serial);

    private async Task ReleaseAsync(string serial, CancellationToken cancellationToken)
    {
        if (_holds.Remove(serial, out var hold))
        {
            _logger.LogInformation(
                "Tempo {Phone}: eventos liberados para o motor {Seconds:0.000} s depois do cabo",
                Sensitive.Mask(serial),
                _time.GetElapsedTime(hold.StartedTimestamp).TotalSeconds);
        }

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
            if (action is NotifyUser { Notice: DeviceNotice.Offline, Serial: { } serial })
            {
                DeferOfflineNotice(serial, action);
                continue;
            }

            await ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(EngineAction action, CancellationToken cancellationToken)
    {
        // O mesmo texto que o DevConsole mostra: log e tela contam as mesmas ações.
        _logger.LogInformation("Ação: {Action}", EngineActionText.Describe(action));
        _observer?.OnAction(action);
        if (action is DoNothing)
        {
            return;
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

    /// <summary>Segura o aviso de offline: ele só sai se o celular continuar offline depois de <see cref="ConnectionCoordinatorOptions.OfflineNoticeDelay"/>.</summary>
    private void DeferOfflineNotice(string serial, EngineAction notice)
    {
        var generation = ++_offlineNoticeGeneration;
        _pendingOfflineNotices[serial] = generation;
        _logger.LogInformation(
            "{Phone} offline: avisa se continuar assim por {Seconds:0} s",
            Sensitive.Mask(serial),
            _options.OfflineNoticeDelay.TotalSeconds);
        _background.Add(OfflineNoticeLaterAsync(serial, generation, notice));
    }

    private async Task OfflineNoticeLaterAsync(string serial, int generation, EngineAction notice)
    {
        try
        {
            await Task.Delay(_options.OfflineNoticeDelay, _time, _stopToken).ConfigureAwait(false);
            _inbox.Writer.TryWrite(new OfflineNoticeMessage(serial, generation, notice));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task OnOfflineNoticeDueAsync(OfflineNoticeMessage message, CancellationToken cancellationToken)
    {
        if (!_pendingOfflineNotices.TryGetValue(message.Serial, out var generation) || generation != message.Generation)
        {
            return;
        }

        _pendingOfflineNotices.Remove(message.Serial);
        PhoneStatus? status;
        lock (_engineSync)
        {
            status = _engine.GetStatus().FirstOrDefault(p => p.Serial == message.Serial);
        }

        // Voltou para "device" (ou sumiu de vez) nesse meio-tempo: não avisa.
        var stillOffline = status is { Phase: PhonePhase.OutOfRange or PhonePhase.Unavailable }
            && status.Connections.Any(c => c.State == AdbDeviceState.Offline);
        if (!stillOffline)
        {
            _logger.LogInformation("{Phone} saiu do offline em menos de {Seconds:0} s: sem aviso", Sensitive.Mask(message.Serial), _options.OfflineNoticeDelay.TotalSeconds);
            return;
        }

        await ExecuteAsync(message.Notice, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tenta os celulares sem Wi-Fi. Quem ainda não tem espera (ex: o adb acabou de voltar) tenta já.</summary>
    private void StartReconnects()
    {
        if (!_adbAvailable)
        {
            return;
        }

        var clock = _time.GetUtcNow();
        foreach (var phone in _catalog.Phones)
        {
            // Também com o cabo plugado: se o servidor do adb reiniciou, ele esqueceu a conexão Wi-Fi,
            // e sem ela tirar o cabo derrubaria o espelhamento.
            if (string.IsNullOrWhiteSpace(phone.LastKnownIp)
                || _catalog.ResolveWifiMode(phone.Serial) == WifiMode.WirelessDebugging
                || _holds.ContainsKey(phone.Serial)
                || _preparing.Contains(phone.Serial)
                || _connections.Values.Any(c => c.Key == phone.Serial && c.Transport != AdbTransport.Usb))
            {
                continue;
            }

            if (!_reconnect.TryGetValue(phone.Serial, out var state))
            {
                state = new ReconnectState(clock, _options.ReconnectInterval);
                _reconnect[phone.Serial] = state;
            }

            if (state.InFlight || clock < state.NextAttempt)
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

        // O mesmo limite curto de cada tentativa da preparação: com um IP que mudou, o connect fica preso.
        using var attemptTimeout = new CancellationTokenSource(_options.Preparer.ConnectAttemptTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, attemptTimeout.Token);
        try
        {
            var result = await _adb.ConnectAsync(ip, _options.TcpipPort, linked.Token).ConfigureAwait(false);
            connected = result.Connected;
            detail = result.Message;
        }
        catch (Exception ex) when (ex is AdbException or IOException)
        {
            connected = false;
            detail = ex.Message;
        }
        catch (OperationCanceledException) when (!_stopToken.IsCancellationRequested)
        {
            connected = false;
            detail = $"connect não respondeu em {_options.Preparer.ConnectAttemptTimeout.TotalSeconds:0} s";
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

    private async Task OnForgetAsync(string serial, CancellationToken cancellationToken)
    {
        var lastIp = _catalog.Find(serial)?.LastKnownIp;
        await _catalog.ForgetAsync(serial, cancellationToken).ConfigureAwait(false);
        _reconnect.Remove(serial);
        ResetScrcpyRetries(serial);
        _pendingOfflineNotices.Remove(serial);

        // Desconecta o Wi-Fi: o da conexão atual e o do último IP conhecido (pode ser o mesmo).
        var wifi = _connections.Values
            .Where(c => c.Key == serial && c.Transport == AdbTransport.WifiTcpip)
            .Select(c => c.AdbSerial)
            .ToList();
        if (lastIp is not null)
        {
            wifi.Add(lastIp + ":" + _options.TcpipPort.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var adbSerial in wifi.Distinct(StringComparer.Ordinal))
        {
            if (_options.DisconnectWifiOnForget && AdbSerialClassifier.TryParseHostPort(adbSerial, out var host, out var port))
            {
                _background.Add(DisconnectAsync(serial, host, port));
            }
        }

        if (!InRange(serial))
        {
            Note($"{Sensitive.Mask(serial)} esquecido");
            return;
        }

        // Ainda ligado: fica ignorado até sair do alcance. Se os eventos estavam retidos (preparação), o motor
        // recebe o "esquecido" antes das conexões, para não abrir.
        _forgotten.Add(serial);
        _holds.Remove(serial);
        Note($"{Sensitive.Mask(serial)} esquecido; fica ignorado até sair do alcance (sem cabo e sem Wi-Fi) e voltar");
        await ApplyAsync(new PhoneForgotten(serial), cancellationToken).ConfigureAwait(false);
        await SyncAsync(serial, cancellationToken).ConfigureAwait(false);
    }

    private async Task DisconnectAsync(string serial, string host, int port)
    {
        using var timeout = new CancellationTokenSource(_options.Preparer.ConnectAttemptTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, timeout.Token);
        try
        {
            var disconnected = await _adb.DisconnectAsync(host, port, linked.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "adb disconnect de {Phone} ({Address}): {Result}",
                Sensitive.Mask(serial),
                Sensitive.MaskAdbSerial(host + ":" + port.ToString(CultureInfo.InvariantCulture)),
                disconnected ? "desconectado" : "não estava conectado");
        }
        catch (Exception ex) when (ex is AdbException or IOException or OperationCanceledException)
        {
            _logger.LogWarning("adb disconnect de {Phone} falhou: {Error}", Sensitive.Mask(serial), Sensitive.Scrub(ex.Message));
        }
    }

    /// <summary>Ao alcance: alguma conexão que não seja offline (a mesma regra do motor).</summary>
    private bool InRange(string serial) =>
        _connections.Values.Any(c => c.Key == serial && c.State != AdbDeviceState.Offline);

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
            WifiPreparationOutcome.NotResponding => $"{phone} não respondeu pelo cabo (o adb do celular pode estar reiniciando); seguindo sem Wi-Fi por enquanto",
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

    private sealed class Hold(int generation, long startedTimestamp)
    {
        public int Generation { get; } = generation;

        /// <summary>Quando o cabo foi visto (para o diagnóstico de tempos).</summary>
        public long StartedTimestamp { get; } = startedTimestamp;

        /// <summary>O tcpip rodou: o adbd do celular está reiniciando.</summary>
        public bool TcpipDone { get; set; }

        /// <summary>A conexão USB caiu (sumiu ou saiu de "device") enquanto segurava.</summary>
        public bool SawUsbDrop { get; set; }
    }

    private enum ReleaseReason
    {
        /// <summary>Depois do tcpip, o USB não caiu: considera estável.</summary>
        UsbStable,

        /// <summary>Limite de <see cref="ConnectionCoordinatorOptions.HoldLimit"/>.</summary>
        Limit,
    }

    private sealed class ReconnectState(DateTimeOffset nextAttempt, TimeSpan delay)
    {
        public DateTimeOffset NextAttempt { get; set; } = nextAttempt;

        public TimeSpan Delay { get; set; } = delay;

        public bool InFlight { get; set; }
    }

    private sealed class ScrcpyRetryState
    {
        /// <summary>Novas tentativas já feitas (ou agendadas) nesta sequência de falhas.</summary>
        public int Attempts { get; set; }

        public int Generation { get; set; }
    }

    private abstract record Message;

    private sealed record ScrcpyRetryMessage(string Serial, int Generation) : Message;

    private sealed record OfflineNoticeMessage(string Serial, int Generation, EngineAction Notice) : Message;

    private sealed record ForgetMessage(string Serial, TaskCompletionSource Done) : Message;

    private sealed record MonitorMessage(DeviceEvent Event) : Message;

    private sealed record InputMessage(EngineInput Input) : Message;

    private sealed record LearnedMessage(string Serial, string? Ip, string? DeviceName) : Message;

    private sealed record PreparedMessage(string Serial, WifiPreparation? Result) : Message;

    private sealed record ReleaseHoldMessage(string Serial, int Generation, ReleaseReason Reason) : Message;

    private sealed record ReadyMessage(string Serial, bool TcpipRestarted) : Message;

    private sealed record ReconnectTickMessage : Message;

    private sealed record ReconnectedMessage(string Serial, bool Connected, string? Detail) : Message;

    private sealed record BarrierMessage(TaskCompletionSource Done) : Message;
}
