using System.Threading.Channels;
using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Decisions;
using Wireflex.Core.Execution;
using Wireflex.Core.Monitoring;
using Wireflex.Core.Tray;
using Wireflex.Tests.Fakes;

namespace Wireflex.Tests.Connection;

/// <summary>
/// Coordenador com adb, executor, config e relógio falsos. Os eventos do monitor entram pela mesma fila
/// do coordenador, e <see cref="ConnectionCoordinator.WhenIdleAsync"/> marca quando tudo foi processado.
/// </summary>
public sealed class ConnectionCoordinatorTests : IAsyncDisposable
{
    private const string Phone = "TESTSERIAL01";
    private const string Ip = "192.0.2.10";
    private const string WifiSerial = "192.0.2.10:5555";
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StableWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OfflineDelay = TimeSpan.FromSeconds(5);

    private readonly TimerTrackingTimeProvider _time = new();
    private readonly ScriptedAdbClient _adb = new();
    private readonly RecordingExecutor _executor = new();
    private readonly RecordingConfigStore _store = new();
    private readonly Channel<DeviceEvent> _monitor = Channel.CreateUnbounded<DeviceEvent>();
    private readonly CancellationTokenSource _stop = new();
    private PhoneCatalog _catalog = null!;
    private ConnectionCoordinator _coordinator = null!;
    private Task? _run;
    private bool _tickTimerSeen;

    public ConnectionCoordinatorTests()
    {
        _adb.Shell["ip route"] = "192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.10\n";
        _adb.Shell["settings get global device_name"] = "Celular de Teste\n";
        _adb.Shell["getprop service.adb.tcp.port"] = "\n";
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.WaitAsync(TestTimeouts.Real);
        }

        _stop.Dispose();
    }

    [Fact]
    public async Task CaboSemTcpip_LigaTcpipEAbrePeloCaboAssimQueOUsbVolta()
    {
        // O tcpip reinicia o adbd: a conexão USB some e volta, e o Wi-Fi aparece.
        _adb.OnTcpip = _ =>
        {
            _coordinator.PostDeviceEvent(new DeviceDisappeared(Usb()));
            _coordinator.PostDeviceEvent(new DeviceAppeared(Usb()));
            _coordinator.PostDeviceEvent(new DeviceAppeared(Wifi()));
        };
        Start(real: true);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));

        // O USB voltou: abre pelo cabo sem esperar o connect, que ainda espera o adbd reiniciar.
        Assert.Equal(1, _adb.Count("tcpip TESTSERIAL01 5555"));
        Assert.Equal(0, _adb.Count("connect"));
        var open = Assert.IsType<OpenScrcpy>(Assert.Single(_executor.Actions));
        Assert.Equal(AdbTransport.Usb, open.Transport);

        // O connect segue em segundo plano.
        await _time.AdvanceWhenTimerCreatedAsync(SettleDelay);
        await IdleAsync();
        Assert.Equal(1, _adb.Count($"connect {WifiSerial}"));
        Assert.Single(_executor.Actions);

        var learned = Assert.Single(_catalog.Phones);
        Assert.Equal(Ip, learned.LastKnownIp);
        Assert.Equal("Celular de Teste", learned.FriendlyName);
        Assert.Equal("Test Phone", learned.Model);
        Assert.Equal(Ip, _store.Last!.FindPhone(Phone)!.LastKnownIp);
    }

    [Fact]
    public async Task DoisCelularesNoCabo_IpDeCadaUmGravado()
    {
        // Regressão: um celular já conhecido, mas sem IP gravado, tem que ganhar o IP como o outro.
        const string other = "TESTSERIAL02";
        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        _adb.ShellFor[$"{other} ip route"] = "192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.20\n";
        var stored = new WireflexConfig();
        stored.Phones.Add(new KnownPhone { Serial = other, Model = "Other Phone" });
        Start(real: true, stored);
        await AdbAvailableAsync();

        _coordinator.PostDeviceEvent(new DeviceAppeared(Usb()));
        await SendAsync(new DeviceAppeared(new DeviceConnection(other, other, AdbTransport.Usb, AdbDeviceState.Device, "Other Phone")));

        Assert.Equal(Ip, _store.Last!.FindPhone(Phone)!.LastKnownIp);
        Assert.Equal("192.0.2.20", _store.Last!.FindPhone(other)!.LastKnownIp);
        Assert.Equal(2, _executor.Actions.OfType<OpenScrcpy>().Count());
    }

    [Fact]
    public async Task Porta5555EIpConhecido_AbreNaHoraSemTcpipEConectaDepois()
    {
        // O connect fica preso (ex: IP errado): o espelhamento não pode esperar por ele.
        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        _adb.ConnectHangs = true;
        Start(real: true);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));

        Assert.Equal(AdbTransport.Usb, Assert.IsType<OpenScrcpy>(Assert.Single(_executor.Actions)).Transport);
        Assert.Equal(0, _adb.Count("tcpip"));
        Assert.Equal(1, _adb.Count($"connect {WifiSerial}"));
    }

    [Fact]
    public async Task TcpipSemOUsbCair_SoltaDepoisDaJanelaDeEstabilidade()
    {
        // Sem OnTcpip: o adbd reiniciou rápido demais para o monitor ver a queda.
        _adb.ConnectHangs = true;
        Start(real: true);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));
        Assert.Empty(_executor.Actions);

        await _time.AdvanceWhenTimerCreatedAsync(StableWindow);
        await IdleAsync();

        Assert.Equal(AdbTransport.Usb, Assert.IsType<OpenScrcpy>(Assert.Single(_executor.Actions)).Transport);
    }

    [Fact]
    public async Task NomeEIp_GravadosAntesDoConnect()
    {
        Start(real: true);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));

        // O tcpip rodou e o connect ainda espera: o IP e o nome já estão no config.json.
        Assert.Equal(0, _adb.Count("connect"));
        Assert.Equal(Ip, _store.Last!.FindPhone(Phone)!.LastKnownIp);
        Assert.Equal("Celular de Teste", _store.Last!.FindPhone(Phone)!.FriendlyName);
        Assert.Equal("Test Phone", _store.Last!.FindPhone(Phone)!.Model);
    }

    [Fact]
    public async Task CelularJaPlugadoNaPartida_TambemPrepara()
    {
        // Na partida, o monitor manda os aparelhos antes do "adb disponível".
        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        Start(real: true);

        await SendAsync(new DeviceAppeared(Usb()));
        await SendAsync(new MonitorStatusChanged(true, null));
        await IdleAsync();

        Assert.Equal(1, _adb.Count("shell TESTSERIAL01 ip route"));
        Assert.Equal(1, _adb.Count($"connect {WifiSerial}"));
        Assert.Equal(Ip, _catalog.Find(Phone)!.LastKnownIp);
    }

    [Fact]
    public async Task CaboComWifiJaConectado_TrocaNaHoraSemTcpip()
    {
        Start(real: true);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Wifi()));

        await SendAsync(new DeviceAppeared(Usb()));
        await IdleAsync();

        Assert.Collection(
            _executor.Actions,
            a => Assert.Equal(AdbTransport.WifiTcpip, Assert.IsType<OpenScrcpy>(a).Transport),
            a => Assert.Equal(AdbTransport.Usb, Assert.IsType<SwitchScrcpy>(a).To));
        Assert.Equal(0, _adb.Count("tcpip"));
        Assert.Equal(0, _adb.Count("connect"));

        // Mesmo sem tcpip, aprende o IP e o nome pelo cabo.
        Assert.Equal("Celular de Teste", _catalog.Find(Phone)!.FriendlyName);
    }

    [Fact]
    public async Task CaboWifiCabo_TrocaSemIntervencao()
    {
        Start(real: true);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Wifi()));
        await SendAsync(new DeviceAppeared(Usb()));
        await IdleAsync();
        _executor.Clear();

        // Tira o cabo: passa para o Wi-Fi.
        await SendAsync(new DeviceDisappeared(Usb()));
        var toWifi = Assert.IsType<SwitchScrcpy>(Assert.Single(_executor.Actions));
        Assert.Equal(AdbTransport.WifiTcpip, toWifi.To);
        _executor.Clear();

        // Pluga de novo: volta para o cabo, sem rodar tcpip de novo.
        await SendAsync(new DeviceAppeared(Usb()));
        await IdleAsync();
        var toUsb = Assert.IsType<SwitchScrcpy>(Assert.Single(_executor.Actions));
        Assert.Equal(AdbTransport.Usb, toUsb.To);
        Assert.Equal(0, _adb.Count("tcpip"));
    }

    [Fact]
    public async Task TcpipJaLigado_NaoRodaDeNovo()
    {
        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        _adb.Connect = (host, port) =>
        {
            _coordinator.PostDeviceEvent(new DeviceAppeared(Wifi()));
            return new AdbConnectResult(true, $"connected to {host}:{port}");
        };
        Start(real: true);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));
        await IdleAsync();

        Assert.Equal(0, _adb.Count("tcpip"));
        Assert.Equal(1, _adb.Count("connect"));
        Assert.Equal(AdbTransport.Usb, Assert.IsType<OpenScrcpy>(Assert.Single(_executor.Actions)).Transport);
    }

    [Fact]
    public async Task CelularSemWifi_AbrePeloCaboSemTcpip()
    {
        _adb.Shell["ip route"] = "10.0.0.0/30 dev rmnet_data0 proto kernel scope link src 10.0.0.2\n";
        _adb.Shell["ip -f inet addr show wlan0"] = string.Empty;
        Start(real: true);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));
        await IdleAsync();

        Assert.Equal(AdbTransport.Usb, Assert.IsType<OpenScrcpy>(Assert.Single(_executor.Actions)).Transport);
        Assert.Equal(0, _adb.Count("tcpip"));
        Assert.Equal(0, _adb.Count("connect"));
    }

    [Fact]
    public async Task CaboNaoVoltaDepoisDoTcpip_NoLimiteAbreComOQueHa()
    {
        // O USB cai e não volta; o Wi-Fi apareceu sozinho. O connect fica preso.
        _adb.ConnectHangs = true;
        _adb.OnTcpip = _ =>
        {
            _coordinator.PostDeviceEvent(new DeviceDisappeared(Usb()));
            _coordinator.PostDeviceEvent(new DeviceAppeared(Wifi()));
        };
        Start(real: true);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Usb()));
        Assert.Empty(_executor.Actions);

        await _time.AdvanceWhenTimerCreatedAsync(HoldLimit);
        await IdleAsync();

        Assert.Equal(AdbTransport.WifiTcpip, Assert.IsType<OpenScrcpy>(Assert.Single(_executor.Actions)).Transport);
    }

    [Fact]
    public async Task ConectarPeloWifiFalha_JaAbriuPeloCabo()
    {
        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        _adb.Connect = (host, port) => new AdbConnectResult(false, $"failed to connect to {host}:{port}");
        Start(real: true);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));

        Assert.Equal(AdbTransport.Usb, Assert.IsType<OpenScrcpy>(Assert.Single(_executor.Actions)).Transport);
        Assert.True(_adb.Count("connect") >= 1);
    }

    [Fact]
    public async Task ModoSimulado_NaoMexeNoCelular()
    {
        Start(real: false);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));

        Assert.IsType<OpenScrcpy>(Assert.Single(_executor.Actions));
        Assert.Empty(_adb.Calls);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task CaboUnauthorized_SoPreparaQuandoAutorizar()
    {
        Start(real: true);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb(AdbDeviceState.Unauthorized)));
        Assert.IsType<NotifyUser>(Assert.Single(_executor.Actions));
        Assert.Empty(_adb.Calls);

        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        await SendAsync(new DeviceStateChanged(Usb(), AdbDeviceState.Unauthorized));
        await IdleAsync();

        Assert.Equal(1, _adb.Count("connect"));
        Assert.IsType<OpenScrcpy>(_executor.Actions[^1]);
    }

    [Fact]
    public async Task Reconexao_TentaOUltimoIpComEsperaCrescente()
    {
        var config = new WireflexConfig();
        config.Phones.Add(new KnownPhone { Serial = Phone, LastKnownIp = Ip });
        _adb.Connect = (host, port) => new AdbConnectResult(false, $"failed to connect to {host}:{port}");
        Start(real: true, config);
        await AdbAvailableAsync();

        // 0 s (adb disponível): tenta. Falhou: a espera dobra para 30 s. 15 s: ainda esperando. 30 s: tenta de novo.
        Assert.Equal(1, _adb.Count($"connect {WifiSerial}"));
        await TickAsync();
        Assert.Equal(1, _adb.Count($"connect {WifiSerial}"));
        await TickAsync();
        Assert.Equal(2, _adb.Count($"connect {WifiSerial}"));
    }

    [Fact]
    public async Task Reconexao_CelularPresenteNaoTenta()
    {
        var config = new WireflexConfig();
        config.Phones.Add(new KnownPhone { Serial = Phone, LastKnownIp = Ip });
        Start(real: true, config);
        await SendAsync(new DeviceAppeared(Wifi()));
        await AdbAvailableAsync();

        await TickAsync();

        Assert.Equal(0, _adb.Count("connect"));
    }

    [Fact]
    public async Task Reconexao_AdbVoltou_TentaNaHoraSemEsperarARodada()
    {
        var config = new WireflexConfig();
        config.Phones.Add(new KnownPhone { Serial = Phone, LastKnownIp = Ip });
        _adb.Connect = (host, port) => new AdbConnectResult(false, $"failed to connect to {host}:{port}");
        Start(real: true, config);

        await AdbAvailableAsync();
        Assert.Equal(1, _adb.Count($"connect {WifiSerial}"));

        // Falhou (a espera dobrou para 30 s), mas o servidor do adb caiu e voltou: tenta de novo já.
        await SendAsync(new MonitorStatusChanged(false, "teste"));
        await SendAsync(new MonitorStatusChanged(true, null));
        Assert.Equal(2, _adb.Count($"connect {WifiSerial}"));
    }

    [Fact]
    public async Task Reconexao_ConnectTravado_DesisteEm3s()
    {
        var config = new WireflexConfig();
        config.Phones.Add(new KnownPhone { Serial = Phone, LastKnownIp = Ip });
        _adb.ConnectHangs = true;
        Start(real: true, config);
        await AdbAvailableAsync();
        Assert.Equal(1, _adb.Count($"connect {WifiSerial}"));

        // O timer da tentativa já foi criado (a tentativa começou dentro do AdbAvailableAsync).
        _time.Advance(TimeSpan.FromSeconds(3));
        await IdleAsync();
        Assert.Contains(_adb.Calls, c => c.StartsWith("connect", StringComparison.Ordinal));

        // Desistiu: não ficou "em andamento" para sempre; com o adb voltando, tenta de novo.
        await SendAsync(new MonitorStatusChanged(false, "teste"));
        await SendAsync(new MonitorStatusChanged(true, null));
        Assert.Equal(2, _adb.Count($"connect {WifiSerial}"));
    }

    [Fact]
    public async Task Reconexao_NoCaboSemWifi_TentaOWifiDeNovo()
    {
        // Ex: adb kill-server com o celular no cabo; o servidor novo não lembra da conexão Wi-Fi.
        var config = new WireflexConfig();
        config.Phones.Add(new KnownPhone { Serial = Phone, LastKnownIp = Ip });
        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        Start(real: true, config);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Usb()));
        await IdleAsync();
        var afterPreparation = _adb.Count("connect");

        await TickAsync();

        Assert.Equal(afterPreparation + 1, _adb.Count($"connect {WifiSerial}"));
        Assert.Equal(0, _adb.Count("tcpip"));
    }

    [Fact]
    public async Task Reconexao_AdbForaDoAr_NaoTenta()
    {
        var config = new WireflexConfig();
        config.Phones.Add(new KnownPhone { Serial = Phone, LastKnownIp = Ip });
        Start(real: true, config);

        await TickAsync();

        Assert.Empty(_adb.Calls);
    }

    [Fact]
    public async Task WifiIdentificado_AprendeOIp()
    {
        Start(real: false);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Wifi()));

        Assert.Equal(Ip, _catalog.Find(Phone)!.LastKnownIp);
    }

    [Fact]
    public async Task RespostaDoExecutor_VoltaParaOMotor()
    {
        Start(real: false);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Usb()));

        _coordinator.Post(new ScrcpyClosedByUser(Phone, Phone));
        await IdleAsync();

        Assert.Equal(PhonePhase.ClosedByUser, Assert.Single(_coordinator.GetStatus()).Phase);
    }

    [Fact]
    public async Task Encerrar_FechaOsScrcpy()
    {
        Start(real: false);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Usb()));

        await _stop.CancelAsync();
        await _run!.WaitAsync(TestTimeouts.Real);

        Assert.True(_executor.Disposed);
    }

    [Fact]
    public async Task ScrcpyFalhaAoAbrir_TentaDeNovoDepoisDeDoisSegundos()
    {
        await MirroringByCableAsync();

        // Ex: logo depois da troca para o Wi-Fi o adb push falha e o scrcpy sai com 1; o estado do adb não muda.
        await FailAtStartAsync();
        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());

        await _time.AdvanceWhenTimerCreatedAsync(RetryDelay);
        await IdleAsync();

        Assert.Equal(2, _executor.Actions.OfType<OpenScrcpy>().Count());
    }

    [Fact]
    public async Task ScrcpyFalhaAoAbrir_NoMaximoTresTentativasSeguidas()
    {
        await MirroringByCableAsync();

        for (var i = 0; i < 3; i++)
        {
            await FailAtStartAsync();
            await _time.AdvanceWhenTimerCreatedAsync(RetryDelay);
            await IdleAsync();
        }

        await FailAtStartAsync();
        _time.Advance(RetryDelay);
        await IdleAsync();

        // A aberta pelo cabo + 3 tentativas.
        Assert.Equal(4, _executor.Actions.OfType<OpenScrcpy>().Count());
    }

    [Fact]
    public async Task ScrcpyAbertoMaisDeDezSegundos_ZeraAsTentativas()
    {
        await UseAllRetriesAsync();

        // O da 3ª tentativa ficou aberto 30 s e caiu: não é falha ao abrir, e a contagem recomeça.
        _coordinator.Post(new ScrcpyExited(Phone, Phone, FailedAtStart: false, Runtime: TimeSpan.FromSeconds(30)));
        await IdleAsync();
        _coordinator.Post(new Reevaluate(Phone, "teste"));
        await IdleAsync();
        var opens = _executor.Actions.OfType<OpenScrcpy>().Count();

        await FailAtStartAsync();
        await _time.AdvanceWhenTimerCreatedAsync(RetryDelay);
        await IdleAsync();

        Assert.Equal(opens + 1, _executor.Actions.OfType<OpenScrcpy>().Count());
    }

    [Fact]
    public async Task ConexaoMuda_ZeraAsTentativas()
    {
        await UseAllRetriesAsync();
        await FailAtStartAsync();

        // O Wi-Fi conecta: o motor abre por conta própria e, se falhar de novo, volta a tentar.
        await SendAsync(new DeviceAppeared(Wifi()));
        var opens = _executor.Actions.OfType<OpenScrcpy>().Count();
        _coordinator.Post(new ScrcpyExited(Phone, Phone, FailedAtStart: true));
        await IdleAsync();
        await _time.AdvanceWhenTimerCreatedAsync(RetryDelay);
        await IdleAsync();

        Assert.Equal(opens + 1, _executor.Actions.OfType<OpenScrcpy>().Count());
    }

    [Fact]
    public async Task ScrcpyFalhaAoAbrir_PausadoNaoReabre()
    {
        await MirroringByCableAsync();
        await FailAtStartAsync();

        _coordinator.Post(new PhonePauseChanged(Phone, true));
        await IdleAsync();
        _time.Advance(RetryDelay);
        await IdleAsync();

        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());
    }

    [Fact]
    public async Task ScrcpyFalhaAoAbrir_ForaDoAlcanceNaoReabre()
    {
        await MirroringByCableAsync();
        await FailAtStartAsync();

        await SendAsync(new DeviceDisappeared(Usb()));
        _time.Advance(RetryDelay);
        await IdleAsync();

        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());
    }

    [Fact]
    public async Task UsuarioFechouAJanela_NaoTentaDeNovo()
    {
        await MirroringByCableAsync();

        _coordinator.Post(new ScrcpyClosedByUser(Phone, Phone));
        await IdleAsync();
        _time.Advance(RetryDelay);
        await IdleAsync();

        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());
        Assert.Equal(PhonePhase.ClosedByUser, Assert.Single(_coordinator.GetStatus()).Phase);
    }

    [Fact]
    public async Task OfflineCurto_NaoAvisa()
    {
        Start(real: false);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Wifi()));

        // Logo depois de tirar o cabo: o Wi-Fi fica offline ~1 s e volta.
        await SendAsync(new DeviceStateChanged(Wifi(AdbDeviceState.Offline), AdbDeviceState.Device));
        await _time.WaitForTimerAsync(OfflineDelay);
        _time.Advance(TimeSpan.FromSeconds(1));
        await SendAsync(new DeviceStateChanged(Wifi(), AdbDeviceState.Offline));
        _time.Advance(OfflineDelay);
        await IdleAsync();

        Assert.Empty(_executor.Actions.OfType<NotifyUser>());
    }

    [Fact]
    public async Task OfflineLongo_AvisaUmaVezDepoisDeCincoSegundos()
    {
        Start(real: false);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Wifi()));

        await SendAsync(new DeviceStateChanged(Wifi(AdbDeviceState.Offline), AdbDeviceState.Device));
        Assert.Empty(_executor.Actions.OfType<NotifyUser>());

        await _time.AdvanceWhenTimerCreatedAsync(OfflineDelay);
        await IdleAsync();

        var notice = Assert.Single(_executor.Actions.OfType<NotifyUser>());
        Assert.Equal(DeviceNotice.Offline, notice.Notice);
    }

    [Fact]
    public async Task OfflineVoltaEFicaOfflineDeNovo_ContaDoUltimoOffline()
    {
        Start(real: false);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Wifi()));

        await SendAsync(new DeviceStateChanged(Wifi(AdbDeviceState.Offline), AdbDeviceState.Device));
        await _time.WaitForTimerAsync(OfflineDelay);
        _time.Advance(TimeSpan.FromSeconds(1));
        await SendAsync(new DeviceStateChanged(Wifi(), AdbDeviceState.Offline));
        _time.Advance(TimeSpan.FromSeconds(1));
        await SendAsync(new DeviceStateChanged(Wifi(AdbDeviceState.Offline), AdbDeviceState.Device));

        // 5 s depois do primeiro offline: o segundo só tem 3 s, ainda não avisa.
        await _time.WaitForTimerAsync(OfflineDelay);
        _time.Advance(TimeSpan.FromSeconds(3));
        await IdleAsync();
        Assert.Empty(_executor.Actions.OfType<NotifyUser>());

        _time.Advance(TimeSpan.FromSeconds(2));
        await IdleAsync();
        Assert.Single(_executor.Actions.OfType<NotifyUser>());
    }

    [Fact]
    public async Task Esquecer_NoCabo_FechaDesconectaEIgnoraAteSairDoAlcance()
    {
        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        var stored = new WireflexConfig();
        stored.Phones.Add(new KnownPhone { Serial = Phone, LastKnownIp = Ip });
        Start(real: true, stored);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Usb()));
        await SendAsync(new DeviceAppeared(Wifi()));
        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());

        await Commands().ForgetAsync(Phone);
        await IdleAsync();

        // Fecha, sai da config e desconecta o Wi-Fi (uma vez: o IP gravado e a conexão atual são o mesmo).
        Assert.Single(_executor.Actions.OfType<CloseScrcpy>());
        Assert.Null(_store.Last!.FindPhone(Phone));
        Assert.Equal(1, _adb.Count($"disconnect {WifiSerial}"));
        Assert.Equal(PhonePhase.Ignored, Assert.Single(_coordinator.GetStatus()).Phase);

        // Ainda no cabo: o Wi-Fi some e outros eventos chegam, e nada abre nem volta para a config.
        await SendAsync(new DeviceDisappeared(Wifi()));
        await SendAsync(new DeviceStateChanged(Usb(), AdbDeviceState.Device));
        _coordinator.Post(new Reevaluate(null, "teste"));
        await IdleAsync();
        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());
        Assert.Null(_store.Last!.FindPhone(Phone));

        // Tirou o cabo (fora do alcance) e plugou de novo: é um celular novo, que abre e é aprendido.
        await SendAsync(new DeviceDisappeared(Usb()));
        await SendAsync(new DeviceAppeared(Usb()));
        Assert.Equal(2, _executor.Actions.OfType<OpenScrcpy>().Count());
        Assert.NotNull(_store.Last!.FindPhone(Phone));
    }

    [Fact]
    public async Task Esquecer_SoPeloWifi_DesconectaENaoReconecta()
    {
        Start(real: true);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Wifi()));
        Assert.Equal(Ip, _catalog.Find(Phone)!.LastKnownIp);

        await Commands().ForgetAsync(Phone);
        await IdleAsync();
        Assert.Single(_executor.Actions.OfType<CloseScrcpy>());
        Assert.Equal(1, _adb.Count($"disconnect {WifiSerial}"));

        // O disconnect derruba o Wi-Fi: fora do alcance, e a reconexão não tem mais o IP.
        await SendAsync(new DeviceDisappeared(Wifi()));
        await TickAsync();
        Assert.Equal(0, _adb.Count("connect"));
        Assert.Null(_catalog.Find(Phone));
    }

    [Fact]
    public async Task Esquecer_ForaDoAlcance_SoTiraDaConfig()
    {
        var stored = new WireflexConfig();
        stored.Phones.Add(new KnownPhone { Serial = Phone, LastKnownIp = Ip });
        Start(real: true, stored);
        await AdbAvailableAsync();

        await Commands().ForgetAsync(Phone);
        await IdleAsync();

        Assert.Null(_store.Last!.FindPhone(Phone));
        Assert.Empty(_coordinator.GetStatus());

        // Volta pelo cabo: abre normalmente.
        await SendAsync(new DeviceAppeared(Usb()));
        await _time.AdvanceWhenTimerCreatedAsync(StableWindow);
        await IdleAsync();
        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());
    }

    [Fact]
    public async Task Desativar_SoPeloWifi_GravaEFecha()
    {
        Start(real: false);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Wifi()));

        await Commands().SetEnabledAsync(Phone, enabled: false);
        await IdleAsync();

        Assert.Equal(ConnectionMode.Never, _catalog.Find(Phone)!.Settings.ConnectionMode);
        Assert.Single(_executor.Actions.OfType<CloseScrcpy>());

        await Commands().SetEnabledAsync(Phone, enabled: true);
        await IdleAsync();
        Assert.Null(_catalog.Find(Phone)!.Settings.ConnectionMode);
        Assert.Equal(2, _executor.Actions.OfType<OpenScrcpy>().Count());
    }

    [Fact]
    public async Task PausarERetomar_SoPeloWifi()
    {
        Start(real: false);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Wifi()));

        Commands().SetPaused(Phone, true);
        await IdleAsync();
        Assert.Equal(PhonePhase.Paused, Assert.Single(_coordinator.GetStatus()).Phase);
        Assert.Single(_executor.Actions.OfType<CloseScrcpy>());

        Commands().SetPaused(Phone, false);
        await IdleAsync();
        Assert.Equal(2, _executor.Actions.OfType<OpenScrcpy>().Count());
    }

    [Fact]
    public async Task ModoGlobal_GravaEReavaliaTodosNaHora()
    {
        Start(real: true, new WireflexConfig());
        await AdbAvailableAsync();
        _adb.Shell["getprop service.adb.tcp.port"] = "5555\n";
        await SendAsync(new DeviceAppeared(Usb()));
        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());

        await Commands().SetGlobalModeAsync(ConnectionMode.Never);
        await IdleAsync();
        Assert.Equal(ConnectionMode.Never, _store.Last!.Global.ConnectionMode);
        Assert.Single(_executor.Actions.OfType<CloseScrcpy>());

        await Commands().SetGlobalModeAsync(ConnectionMode.Ask);
        await IdleAsync();
        Assert.Single(_executor.Actions.OfType<AskUser>());

        await Commands().SetGlobalModeAsync(ConnectionMode.Automatic);
        await IdleAsync();
        Assert.Equal(ConnectionMode.Automatic, _store.Last!.Global.ConnectionMode);
    }

    [Fact]
    public async Task AbrirAgora_DepoisDeFecharAJanela()
    {
        await MirroringByCableAsync();
        _coordinator.Post(new ScrcpyClosedByUser(Phone, Phone));
        await IdleAsync();

        Commands().OpenNow(Phone);
        await IdleAsync();

        Assert.Equal(2, _executor.Actions.OfType<OpenScrcpy>().Count());
        Assert.Equal(PhonePhase.Mirroring, Assert.Single(_coordinator.GetStatus()).Phase);
    }

    [Fact]
    public async Task CadaAcao_VaiParaOLogComOTextoDaTela()
    {
        var logs = new RecordingLoggerFactory();
        var observer = new RecordingObserver();
        Start(real: false, logs: logs, observer: observer);
        await AdbAvailableAsync();

        await SendAsync(new DeviceAppeared(Usb()));
        await SendAsync(new DeviceDisappeared(Usb()));

        // Inclusive o NaoFazerNada, que a tela mostra em cinza.
        Assert.Contains(observer.Actions, a => a is OpenScrcpy);
        Assert.Contains(observer.Actions, a => a is DoNothing);
        var logged = logs.Logger.Messages.Where(m => m.StartsWith("Ação: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(observer.Actions.Select(a => "Ação: " + EngineActionText.Describe(a)), logged);
        Assert.All(logged, m => Assert.DoesNotContain(Phone, m));
    }

    private void Start(bool real, WireflexConfig? stored = null, RecordingLoggerFactory? logs = null, IConnectionObserver? observer = null)
    {
        _catalog = new PhoneCatalog(stored ?? new WireflexConfig(), real ? _store : null);
        _coordinator = new ConnectionCoordinator(
            new DecisionEngine(serial => _catalog.ResolveConnectionMode(serial)),
            _executor,
            _adb,
            _catalog,
            new ConnectionCoordinatorOptions
            {
                PrepareWifi = real,
                ReconnectWifi = real,
                DisconnectWifiOnForget = real,
                HoldLimit = HoldLimit,
                UsbStableWindow = StableWindow,
                Preparer = new WifiPreparerOptions { TcpipSettleDelay = SettleDelay },
                ReconnectInterval = Interval,
            },
            observer,
            logs,
            timeProvider: _time);
        _run = _coordinator.RunAsync(_monitor.Reader, _stop.Token);
    }

    private Task AdbAvailableAsync() => SendAsync(new MonitorStatusChanged(true, null));

    private TrayCommands Commands() => new(_coordinator, _catalog);

    private async Task MirroringByCableAsync()
    {
        Start(real: false);
        await AdbAvailableAsync();
        await SendAsync(new DeviceAppeared(Usb()));
        Assert.Single(_executor.Actions.OfType<OpenScrcpy>());
    }

    /// <summary>O scrcpy aberto sai com 1 logo ao abrir (o executor informa assim).</summary>
    private async Task FailAtStartAsync()
    {
        var open = _executor.Actions.OfType<OpenScrcpy>().Last();
        _coordinator.Post(new ScrcpyExited(Phone, open.AdbSerial, FailedAtStart: true, Runtime: TimeSpan.FromSeconds(0.4)));
        await IdleAsync();
    }

    /// <summary>Abre pelo cabo e gasta as 3 tentativas (a 3ª fica aberta).</summary>
    private async Task UseAllRetriesAsync()
    {
        await MirroringByCableAsync();
        for (var i = 0; i < 3; i++)
        {
            await FailAtStartAsync();
            await _time.AdvanceWhenTimerCreatedAsync(RetryDelay);
            await IdleAsync();
        }

        Assert.Equal(4, _executor.Actions.OfType<OpenScrcpy>().Count());
    }

    private async Task SendAsync(DeviceEvent deviceEvent)
    {
        _coordinator.PostDeviceEvent(deviceEvent);
        await IdleAsync();
    }

    /// <summary>Espera a fila esvaziar duas vezes: o que foi processado pode ter posto mais coisa na fila.</summary>
    private async Task IdleAsync()
    {
        await _coordinator.WhenIdleAsync().WaitAsync(TestTimeouts.Real);
        await _coordinator.WhenIdleAsync().WaitAsync(TestTimeouts.Real);
    }

    /// <summary>Avança uma rodada de reconexão e espera ela ser processada.</summary>
    private async Task TickAsync()
    {
        if (!_tickTimerSeen)
        {
            await _time.WaitForTimerAsync(Interval);
        }

        _time.Advance(Interval);

        // O próximo timer só é criado depois de a rodada entrar na fila.
        await _time.WaitForTimerAsync(Interval);
        _tickTimerSeen = true;
        await IdleAsync();
    }

    private static DeviceConnection Usb(AdbDeviceState state = AdbDeviceState.Device) =>
        new(Phone, Phone, AdbTransport.Usb, state, "Test Phone");

    private static DeviceConnection Wifi(AdbDeviceState state = AdbDeviceState.Device) =>
        new(WifiSerial, Phone, AdbTransport.WifiTcpip, state, "Test Phone");

    /// <summary>O que o DevConsole receberia para mostrar na tela.</summary>
    private sealed class RecordingObserver : IConnectionObserver
    {
        private readonly List<EngineAction> _actions = [];

        public IReadOnlyList<EngineAction> Actions
        {
            get
            {
                lock (_actions)
                {
                    return _actions.ToList();
                }
            }
        }

        public void OnDeviceEvent(DeviceEvent deviceEvent)
        {
        }

        public void OnAction(EngineAction action)
        {
            lock (_actions)
            {
                _actions.Add(action);
            }
        }

        public void OnNote(string message)
        {
        }
    }

    private sealed class RecordingExecutor : IActionExecutor
    {
        private readonly List<EngineAction> _actions = [];

        public bool Disposed { get; private set; }

        public IReadOnlyList<EngineAction> Actions
        {
            get
            {
                lock (_actions)
                {
                    return _actions.ToList();
                }
            }
        }

        public void Clear()
        {
            lock (_actions)
            {
                _actions.Clear();
            }
        }

        public Task ExecuteAsync(EngineAction action, CancellationToken cancellationToken = default)
        {
            lock (_actions)
            {
                _actions.Add(action);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingConfigStore : IConfigStore
    {
        public int Saves { get; private set; }

        public WireflexConfig? Last { get; private set; }

        public Task<WireflexConfig> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WireflexConfig());

        public Task SaveAsync(WireflexConfig config, CancellationToken cancellationToken = default)
        {
            Saves++;
            Last = config;
            return Task.CompletedTask;
        }
    }
}
