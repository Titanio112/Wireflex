using System.Threading.Channels;
using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Decisions;
using Wireflex.Core.Execution;
using Wireflex.Core.Scrcpy;
using Wireflex.Tests.Fakes;

namespace Wireflex.Tests.Execution;

/// <summary>Executor com scrcpy falso: nenhum processo real é aberto.</summary>
public sealed class ScrcpyActionExecutorTests : IAsyncDisposable
{
    private const string Phone = "TESTSERIAL01";
    private const string Wifi = "192.0.2.10:5555";

    private readonly FakeScrcpyLauncher _launcher = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Channel<EngineInput> _reports = Channel.CreateUnbounded<EngineInput>();
    private readonly ScrcpyActionExecutor _executor;

    public ScrcpyActionExecutorTests()
    {
        _executor = new ScrcpyActionExecutor(
            _launcher,
            _notifier,
            serial => $"Wireflex - {serial}",
            input => _reports.Writer.TryWrite(input));
    }

    public async ValueTask DisposeAsync() => await _executor.DisposeAsync();

    [Fact]
    public async Task Abrir_IniciaOScrcpyNaConexaoEscolhida()
    {
        await _executor.ExecuteAsync(new OpenScrcpy(Phone, Phone, AdbTransport.Usb));

        var process = Assert.Single(_launcher.Started);
        Assert.Equal(Phone, process.Request.AdbSerial);
        Assert.Equal($"Wireflex - {Phone}", process.Request.WindowTitle);
        Assert.Equal(new ScrcpyStarted(Phone, Phone), await NextReportAsync());
    }

    [Fact]
    public void Argumentos_UsamSerialETitulo()
    {
        var args = ScrcpyLauncher.BuildArguments(new ScrcpyLaunchRequest(Phone, Wifi, "Wireflex - Teste"));

        Assert.Equal(["--serial", Wifi, "--window-title", "Wireflex - Teste"], args);
    }

    [Theory]
    [InlineData(0, ScrcpyExitKind.ClosedByUser)]
    [InlineData(1, ScrcpyExitKind.StartFailed)]
    [InlineData(2, ScrcpyExitKind.DeviceDisconnected)]
    [InlineData(-1, ScrcpyExitKind.Other)]
    public void CodigoDeSaida_SegueODocumentadoNoScrcpy(int code, ScrcpyExitKind kind)
    {
        Assert.Equal(kind, ScrcpyExit.Classify(code));
    }

    [Fact]
    public async Task UsuarioFechaAJanela_InformaFechadoPeloUsuario()
    {
        await OpenAsync();

        _launcher.Started[0].Exit(0);

        Assert.Equal(new ScrcpyClosedByUser(Phone, Phone), await NextReportAsync());
        Assert.Empty(_executor.Mirroring);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ScrcpyTerminaComErro_InformaQueEncerrouSozinho(int code)
    {
        await OpenAsync();

        _launcher.Started[0].Exit(code);

        Assert.Equal(new ScrcpyExited(Phone, Phone), await NextReportAsync());
    }

    [Fact]
    public async Task Trocar_FechaOAntigoSemInformarEAbreNaNovaConexao()
    {
        await OpenAsync();

        await _executor.ExecuteAsync(new SwitchScrcpy(Phone, Wifi, AdbTransport.Usb, AdbTransport.WifiTcpip));

        Assert.True(_launcher.Started[0].Killed);
        Assert.Equal(Wifi, _launcher.Started[1].Request.AdbSerial);
        Assert.Equal(new ScrcpyStarted(Phone, Wifi), await NextReportAsync());
        Assert.False(_reports.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Fechar_NaoInformaNada()
    {
        await OpenAsync();

        await _executor.ExecuteAsync(new CloseScrcpy(Phone, "celular pausado"));

        Assert.True(_launcher.Started[0].Killed);
        Assert.False(_reports.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ScrcpyNaoAbre_AvisaEInformaQueEncerrou()
    {
        _launcher.FailWith = "Não foi possível iniciar o scrcpy.";

        await _executor.ExecuteAsync(new OpenScrcpy(Phone, Phone, AdbTransport.Usb));

        Assert.Single(_notifier.Failures);
        Assert.Equal(new ScrcpyExited(Phone, Phone), await NextReportAsync());
    }

    [Fact]
    public async Task Encerrar_FechaTodosSemInformar()
    {
        await OpenAsync();
        await _executor.ExecuteAsync(new OpenScrcpy("TESTSERIAL02", "TESTSERIAL02", AdbTransport.Usb));
        await NextReportAsync();

        await _executor.DisposeAsync();

        Assert.All(_launcher.Started, p => Assert.True(p.Killed));
        Assert.False(_reports.Reader.TryRead(out _));

        // Depois de encerrar, nada abre.
        await _executor.ExecuteAsync(new OpenScrcpy(Phone, Phone, AdbTransport.Usb));
        Assert.Equal(2, _launcher.Started.Count);
    }

    [Fact]
    public async Task PerguntaEAviso_VaoParaONotificador()
    {
        await _executor.ExecuteAsync(new AskUser(Phone, Phone, AdbTransport.Usb));
        await _executor.ExecuteAsync(new NotifyUser(Phone, DeviceNotice.Unauthorized));

        Assert.Single(_notifier.Questions);
        Assert.Single(_notifier.Notices);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task ComOMotor_JanelaFechadaSoReabreDepoisDeSairDoAlcance()
    {
        var engine = new DecisionEngine(_ => ConnectionMode.Automatic);

        await RunAsync(engine, new ConnectionAdded(Phone, Phone, AdbTransport.Usb, AdbDeviceState.Device));
        Assert.Single(_launcher.Started);

        // Usuário fecha a janela: com o celular ainda no cabo, não reabre.
        _launcher.Started[0].Exit(0);
        await RunAsync(engine, await NextReportAsync());
        await RunAsync(engine, new ConnectionAdded(Phone, Wifi, AdbTransport.WifiTcpip, AdbDeviceState.Device));
        Assert.Single(_launcher.Started);

        // Sai do alcance (sem cabo e sem Wi-Fi) e volta: reabre.
        await RunAsync(engine, new ConnectionRemoved(Phone, Wifi));
        await RunAsync(engine, new ConnectionRemoved(Phone, Phone));
        await RunAsync(engine, new ConnectionAdded(Phone, Phone, AdbTransport.Usb, AdbDeviceState.Device));
        Assert.Equal(2, _launcher.Started.Count);
    }

    [Fact]
    public async Task ComOMotor_TirarOCaboComScrcpyAberto_PassaParaOWifi()
    {
        var engine = new DecisionEngine(_ => ConnectionMode.Automatic);
        await RunAsync(engine, new ConnectionAdded(Phone, Wifi, AdbTransport.WifiTcpip, AdbDeviceState.Device));
        await RunAsync(engine, new ConnectionAdded(Phone, Phone, AdbTransport.Usb, AdbDeviceState.Device));
        Assert.Equal(Phone, _launcher.Started[^1].Request.AdbSerial);

        // O scrcpy do cabo sai com 2 (desconectado) antes do adb informar que o cabo saiu.
        _launcher.Started[^1].Exit(2);
        await RunAsync(engine, await NextReportAsync());
        await RunAsync(engine, new ConnectionRemoved(Phone, Phone));

        Assert.Equal(Wifi, _launcher.Started[^1].Request.AdbSerial);
        Assert.True(_launcher.Started[^1].Running);
    }

    private async Task OpenAsync()
    {
        await _executor.ExecuteAsync(new OpenScrcpy(Phone, Phone, AdbTransport.Usb));
        await NextReportAsync();
    }

    /// <summary>Passa a entrada ao motor e executa as ações; os relatos de "scrcpy abriu" voltam ao motor.</summary>
    private async Task RunAsync(DecisionEngine engine, EngineInput input)
    {
        foreach (var action in engine.Handle(input))
        {
            await _executor.ExecuteAsync(action);
        }

        while (_reports.Reader.TryRead(out var report))
        {
            Assert.IsType<ScrcpyStarted>(report);
            engine.Handle(report);
        }
    }

    private async Task<EngineInput> NextReportAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Real);
        return await _reports.Reader.ReadAsync(timeout.Token);
    }
}
