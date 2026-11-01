using System.Text;
using System.Threading.Channels;
using Wireflex.Core.Adb;
using Wireflex.Core.Logging;
using Wireflex.Tests.Fakes;

namespace Wireflex.Tests.Adb;

/// <summary>
/// Cliente do adb contra um servidor falso em memória: sem rede, sem processo e sem espera real.
/// Seriais e IPs são fictícios.
/// </summary>
public sealed class AdbClientTests
{
    private const string Serial = "TESTSERIAL01";
    private const string Line = "TESTSERIAL01           device usb:1-1 product:test model:Test_Phone device:test transport_id:1\n";

    private readonly FakeAdbServer _server = new();
    private readonly FakeAdbServerLauncher _launcher;
    private readonly AdbClient _client;

    public AdbClientTests()
    {
        _launcher = new FakeAdbServerLauncher(_server);
        _server.Reply("host:version", "0029");
        _client = new AdbClient(_server, _launcher);
    }

    [Fact]
    public void EncodeRequest_UsaTamanhoHexDeQuatroDigitos()
    {
        Assert.Equal("000Chost:version", Encoding.ASCII.GetString(AdbWire.EncodeRequest("host:version")));
    }

    [Theory]
    [InlineData("Android Debug Bridge version 1.0.41\nVersion 37.0.1-15733141\n", 41)]
    [InlineData("Android Debug Bridge version 1.0.39\r\nRevision 3db08f2c6889-android\r\n", 39)]
    [InlineData("algo inesperado", null)]
    public void ParseClientVersion_LeOUltimoNumero(string output, int? expected)
    {
        Assert.Equal(expected, AdbServerLauncher.ParseClientVersion(output));
    }

    [Fact]
    public async Task Track_PeloSocket_DecodificaCadaMensagem()
    {
        var sides = AcceptTrack();
        await using var track = _client.TrackDevicesAsync().GetAsyncEnumerator();

        var first = track.MoveNextAsync();
        var side = await sides.ReadAsync();
        await side.FrameAsync(Line);
        Assert.True(await first);
        Assert.Equal(Serial, Assert.Single(track.Current).Serial);

        await side.FrameAsync(string.Empty);
        Assert.True(await track.MoveNextAsync());
        Assert.Empty(track.Current);

        Assert.Contains("host:track-devices-l", _server.Requests);
    }

    [Fact]
    public async Task Track_MensagensQuebradasEmVariosPedacos_JuntaAntesDeDecodificar()
    {
        // Regressão do bug do \r\n: o cabeçalho conta o tamanho exato; aqui nada é convertido no caminho.
        var sides = AcceptTrack();
        await using var track = _client.TrackDevicesAsync().GetAsyncEnumerator();
        var next = track.MoveNextAsync();
        var side = await sides.ReadAsync();

        var frame = $"{Encoding.ASCII.GetByteCount(Line):x4}{Line}";
        await side.RawAsync(frame[..3]);
        await side.RawAsync(frame[3..20]);
        await side.RawAsync(frame[20..]);

        Assert.True(await next);
        var device = Assert.Single(track.Current);
        Assert.Equal(AdbDeviceState.Device, device.State);
        Assert.Equal("Test_Phone", device.Model);
    }

    [Fact]
    public async Task Track_ServidorFechaAConexao_FluxoTerminaSemErro()
    {
        var sides = AcceptTrack();
        await using var track = _client.TrackDevicesAsync().GetAsyncEnumerator();
        var next = track.MoveNextAsync();
        var side = await sides.ReadAsync();
        await side.FrameAsync(Line);
        Assert.True(await next);

        _server.Kill();

        Assert.False(await track.MoveNextAsync());
    }

    [Fact]
    public async Task Track_ConexaoDerrubada_FluxoTerminaSemErro()
    {
        var connector = new BrokenTrackConnector(_server);
        var client = new AdbClient(connector, _launcher);
        AcceptTrack();

        await using var track = client.TrackDevicesAsync().GetAsyncEnumerator();

        Assert.False(await track.MoveNextAsync());
    }

    [Fact]
    public async Task Track_ServidorParado_SobeComStartServerAntes()
    {
        _server.Running = false;
        var sides = AcceptTrack();
        await using var track = _client.TrackDevicesAsync().GetAsyncEnumerator();
        var next = track.MoveNextAsync();
        var side = await sides.ReadAsync();
        await side.FrameAsync(Line);

        Assert.True(await next);
        Assert.Equal(1, _launcher.StartServerCalls);
    }

    [Fact]
    public async Task Track_ServidorNaoSobe_LancaAdbExceptionDepoisDoLimite()
    {
        var time = new TimerTrackingTimeProvider();
        var client = new AdbClient(_server, _launcher, timeProvider: time);
        _server.Running = false;
        _launcher.OnStartServer = () => _server.Running = false;
        AcceptTrack();

        await using var track = client.TrackDevicesAsync().GetAsyncEnumerator();
        var next = track.MoveNextAsync().AsTask();
        await time.WaitForTimerAsync(TimeSpan.FromMilliseconds(100));
        time.Advance(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<AdbException>(() => next.WaitAsync(TestTimeouts.Real));
    }

    [Fact]
    public async Task Track_ServidorDemoraUmInstante_TentaDeNovoLogoSemEsperaCrescente()
    {
        var time = new TimerTrackingTimeProvider();
        var client = new AdbClient(_server, _launcher, timeProvider: time);
        _server.Running = false;
        _launcher.OnStartServer = () => _server.RefuseNext = 2;
        var sides = AcceptTrack();

        await using var track = client.TrackDevicesAsync().GetAsyncEnumerator();
        var next = track.MoveNextAsync().AsTask();
        await time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromMilliseconds(100));
        await time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromMilliseconds(100));
        await (await sides.ReadAsync()).FrameAsync(Line);

        Assert.True(await next.WaitAsync(TestTimeouts.Real));
        Assert.Equal(1, _launcher.StartServerCalls);
    }

    [Fact]
    public async Task Track_VersaoDiferenteDoAdbExe_ReiniciaOServidorSoUmaVez()
    {
        _launcher.ClientVersion = 40;
        var sides = AcceptTrack();

        for (var i = 0; i < 2; i++)
        {
            await using var track = _client.TrackDevicesAsync().GetAsyncEnumerator();
            var next = track.MoveNextAsync();
            var side = await sides.ReadAsync();
            await side.FrameAsync(string.Empty);
            Assert.True(await next);
        }

        Assert.Equal(1, _launcher.StartServerCalls);
        Assert.Equal(1, _launcher.VersionCalls);
    }

    [Fact]
    public async Task Track_MesmaVersao_NaoMexeNoServidor()
    {
        var sides = AcceptTrack();
        await using var track = _client.TrackDevicesAsync().GetAsyncEnumerator();
        var next = track.MoveNextAsync();
        await (await sides.ReadAsync()).FrameAsync(string.Empty);
        Assert.True(await next);

        Assert.Equal(0, _launcher.StartServerCalls);
    }

    [Fact]
    public async Task Track_ServidorRecusa_LancaAdbExceptionComOMotivo()
    {
        _server.Fail("host:track-devices-l", "unknown host service");
        await using var track = _client.TrackDevicesAsync().GetAsyncEnumerator();

        var error = await Assert.ThrowsAsync<AdbException>(async () => await track.MoveNextAsync());
        Assert.Contains("unknown host service", error.Message);
    }

    [Fact]
    public async Task Track_Cancelado_FechaAConexao()
    {
        var sides = AcceptTrack();
        using var cancel = new CancellationTokenSource();
        await using var track = _client.TrackDevicesAsync(cancel.Token).GetAsyncEnumerator();
        var next = track.MoveNextAsync();
        var side = await sides.ReadAsync();

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
        await track.DisposeAsync();
        await side.WhenClosed.WaitAsync(TestTimeouts.Real);
    }

    [Fact]
    public async Task ListDevices_LeARespostaComTamanho()
    {
        _server.Reply("host:devices-l", Line);

        var devices = await _client.ListDevicesAsync();

        Assert.Equal(Serial, Assert.Single(devices).Serial);
    }

    [Fact]
    public async Task ListDevices_SeriaisVistosPassamASerMascaradosEmQualquerTexto()
    {
        _server.Reply("host:devices-l", "TESTSERIAL71           device usb:1-1 product:test model:Test_Phone device:test transport_id:1\n");

        await _client.ListDevicesAsync();

        Assert.Equal("falhou em ***AL71 agora", Sensitive.Scrub("falhou em TESTSERIAL71 agora"));
    }

    [Fact]
    public async Task GetSerialNumber_EscolheOAparelhoERodaGetprop()
    {
        _server.Accept("host:transport:192.0.2.10:5555");
        _server.Output("shell:getprop ro.serialno", "TESTSERIAL01\n");

        var serial = await _client.GetSerialNumberAsync("192.0.2.10:5555");

        Assert.Equal(Serial, serial);
        Assert.Equal(["host:transport:192.0.2.10:5555", "shell:getprop ro.serialno"], _server.Requests);
    }

    [Fact]
    public async Task GetSerialNumber_ServidorParado_RetornaNullSemSubirOServidor()
    {
        // Regra da Etapa 1: identificar não pode subir o servidor do adb sozinho.
        _server.Running = false;

        Assert.Null(await _client.GetSerialNumberAsync("192.0.2.10:5555"));
        Assert.Equal(0, _launcher.StartServerCalls);
    }

    [Fact]
    public async Task GetSerialNumber_AparelhoNaoEncontrado_RetornaNull()
    {
        _server.Fail("host:transport:*", "device '192.0.2.10:5555' not found");

        Assert.Null(await _client.GetSerialNumberAsync("192.0.2.10:5555"));
    }

    [Fact]
    public async Task Shell_FalhaNoAparelho_MensagemNaoMostraOIp()
    {
        _server.Fail("host:transport:*", "device '192.0.2.10:5555' not found");

        var error = await Assert.ThrowsAsync<AdbException>(() => _client.ShellAsync("192.0.2.10:5555", "ip route"));

        Assert.DoesNotContain("192.0.2.10", error.Message);
        Assert.Contains("not found", error.Message);
    }

    [Fact]
    public async Task Shell_AparelhoNaoResponde_EstouraOTempo()
    {
        var time = new TimerTrackingTimeProvider();
        var client = new AdbClient(_server, _launcher, timeProvider: time);
        _server.Accept("host:transport:*");
        _server.On("shell:*", side => side.OkayAsync());

        var shell = client.ShellAsync(Serial, "ip route");
        await time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<AdbException>(() => shell.WaitAsync(TestTimeouts.Real));
        Assert.Contains("não respondeu", error.Message);
    }

    [Fact]
    public async Task EnableTcpip_PedeAPortaAoAparelho()
    {
        _server.Accept("host:transport:*");
        _server.Output("tcpip:5555", "restarting in TCP mode port: 5555\n");

        var message = await _client.EnableTcpipAsync(Serial, 5555);

        Assert.Equal("restarting in TCP mode port: 5555", message);
        Assert.Equal(["host:transport:TESTSERIAL01", "tcpip:5555"], _server.Requests);
    }

    [Theory]
    [InlineData("connected to 192.0.2.10:5555", true)]
    [InlineData("already connected to 192.0.2.10:5555", true)]
    [InlineData("failed to connect to '192.0.2.10:5555': Connection refused", false)]
    [InlineData("cannot connect to 192.0.2.10:5555: Connection timed out", false)]
    public async Task Connect_ResultadoVemNoTexto(string reply, bool connected)
    {
        _server.Reply("host:connect:192.0.2.10:5555", reply);

        var result = await _client.ConnectAsync("192.0.2.10", 5555);

        Assert.Equal(connected, result.Connected);
        Assert.Equal(reply, result.Message);
    }

    [Fact]
    public async Task Connect_ServidorParado_NaoSobeOServidor()
    {
        _server.Running = false;

        await Assert.ThrowsAsync<AdbException>(() => _client.ConnectAsync("192.0.2.10", 5555));
        Assert.Equal(0, _launcher.StartServerCalls);
    }

    [Fact]
    public async Task Disconnect_Desconecta()
    {
        _server.Reply("host:disconnect:192.0.2.10:5555", "disconnected 192.0.2.10:5555");

        Assert.True(await _client.DisconnectAsync("192.0.2.10", 5555));
    }

    [Fact]
    public async Task Disconnect_ConexaoQueNaoExiste_DevolveFalse()
    {
        // Resposta real do servidor do adb quando não tem essa conexão.
        _server.Fail("host:disconnect:192.0.2.10:5555", "no such device '192.0.2.10:5555'");

        Assert.False(await _client.DisconnectAsync("192.0.2.10", 5555));
    }

    [Fact]
    public async Task Disconnect_ServidorParado_NaoSobeOServidor()
    {
        _server.Running = false;

        await Assert.ThrowsAsync<AdbException>(() => _client.DisconnectAsync("192.0.2.10", 5555));
        Assert.Equal(0, _launcher.StartServerCalls);
    }

    [Fact]
    public void DescribeService_MascaraSerialEIp()
    {
        Assert.Equal("host:transport:***AL01", AdbWire.DescribeService("host:transport:TESTSERIAL01"));
        Assert.DoesNotContain("192.0.2.10", AdbWire.DescribeService("host:connect:192.0.2.10:5555"));
    }

    /// <summary>Como no <c>adb kill-server</c> real: depois do OKAY, a leitura falha com IOException (conexão resetada).</summary>
    private sealed class BrokenTrackConnector(FakeAdbServer server) : IAdbServerConnector
    {
        private int _connections;

        public async Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
        {
            var stream = await server.ConnectAsync(cancellationToken);

            // 1ª conexão: host:version; 2ª: o track-devices, que cai.
            return Interlocked.Increment(ref _connections) == 2 ? new ResetAfterOkayStream(stream!) : stream;
        }
    }

    private sealed class ResetAfterOkayStream(Stream inner) : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // A primeira leitura traz o OKAY; a próxima é a conexão resetada.
            if (_reads++ > 0)
            {
                throw new IOException("Unable to read data from the transport connection.");
            }

            return await inner.ReadAsync(buffer[..4], cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Aceita o track-devices e entrega o lado do servidor para o teste empurrar mensagens.</summary>
    private ChannelReader<FakeAdbSide> AcceptTrack()
    {
        var sides = Channel.CreateUnbounded<FakeAdbSide>();
        _server.On("host:track-devices-l", async side =>
        {
            await side.OkayAsync();
            sides.Writer.TryWrite(side);
            await side.WhenClosed;
        });
        return sides.Reader;
    }
}
