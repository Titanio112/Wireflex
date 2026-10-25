using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Tests.Fakes;

namespace Wireflex.Tests.Connection;

/// <summary>Leitura da saída do shell e preparação do Wi-Fi com adb falso. IPs fictícios (192.0.2.x).</summary>
public sealed class WifiPreparerTests
{
    private const string Usb = "TESTSERIAL01";

    [Theory]
    [InlineData("192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.10 \n", "192.0.2.10")]
    [InlineData("10.0.0.0/30 dev rmnet_data0 proto kernel scope link src 10.0.0.2\n192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.10\n", "192.0.2.10")]
    [InlineData("192.0.2.0/24 dev wlan1 proto kernel scope link src 192.0.2.11", "192.0.2.11")]
    [InlineData("10.0.0.0/30 dev rmnet_data0 proto kernel scope link src 10.0.0.2\n", null)]
    [InlineData("", null)]
    [InlineData("192.0.2.0/24 dev wlan0 proto kernel scope link src", null)]
    public void IpRoute_PegaOIpDaInterfaceWlan(string output, string? expected)
    {
        Assert.Equal(expected, AndroidShellParser.ParseWifiIpFromRoutes(output));
    }

    [Theory]
    [InlineData("30: wlan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500\n    inet 192.0.2.10/24 brd 192.0.2.255 scope global wlan0\n", "192.0.2.10")]
    [InlineData("Device \"wlan0\" does not exist.\n", null)]
    public void IpAddr_PegaOInet(string output, string? expected)
    {
        Assert.Equal(expected, AndroidShellParser.ParseInetAddress(output));
    }

    [Theory]
    [InlineData("Celular de Teste\n", "Celular de Teste")]
    [InlineData("null\n", null)]
    [InlineData("  ", null)]
    public void NomeDoAparelho(string output, string? expected)
    {
        Assert.Equal(expected, AndroidShellParser.ParseDeviceName(output));
    }

    [Fact]
    public async Task SoAprender_NaoMexeNoModoTcpip()
    {
        var adb = Scripted();

        var result = await new WifiPreparer(adb).PrepareAsync(Usb, connectWifi: false, CancellationToken.None);

        Assert.Equal(WifiPreparationOutcome.LearnedOnly, result.Outcome);
        Assert.Equal("192.0.2.10", result.Ip);
        Assert.Equal("Celular de Teste", result.DeviceName);
        Assert.Equal(0, adb.Count("tcpip"));
        Assert.Equal(0, adb.Count("connect"));
    }

    [Fact]
    public async Task IpRouteFalha_UsaIpAddrComoReserva()
    {
        var adb = Scripted();
        adb.Shell.Remove("ip route");
        adb.Shell["ip -f inet addr show wlan0"] = "    inet 192.0.2.10/24 scope global wlan0\n";
        adb.Shell["getprop service.adb.tcp.port"] = "5555";

        var result = await new WifiPreparer(adb).PrepareAsync(Usb, connectWifi: true, CancellationToken.None);

        Assert.Equal(WifiPreparationOutcome.Connected, result.Outcome);
        Assert.False(result.TcpipEnabled);
    }

    [Fact]
    public async Task TcpipDesligado_LigaEConectaDepoisDaEspera()
    {
        var adb = Scripted();
        var time = new TimerTrackingTimeProvider();

        var prepare = new WifiPreparer(adb, timeProvider: time).PrepareAsync(Usb, connectWifi: true, CancellationToken.None);
        await time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromSeconds(1));
        var result = await prepare.WaitAsync(TestTimeouts.Real);

        Assert.Equal(WifiPreparationOutcome.Connected, result.Outcome);
        Assert.True(result.TcpipEnabled);
        Assert.Equal(["tcpip TESTSERIAL01 5555", "connect 192.0.2.10:5555"], adb.Calls.Where(c => !c.StartsWith("shell", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SemWifi_NaoLigaTcpip()
    {
        var adb = Scripted();
        adb.Shell["ip route"] = string.Empty;
        adb.Shell["ip -f inet addr show wlan0"] = string.Empty;

        var result = await new WifiPreparer(adb).PrepareAsync(Usb, connectWifi: true, CancellationToken.None);

        Assert.Equal(WifiPreparationOutcome.NoWifi, result.Outcome);
        Assert.Equal(0, adb.Count("tcpip"));
    }

    [Fact]
    public async Task CelularNaoResponde_NaoDizQueEstaSemWifi()
    {
        var adb = Scripted();
        adb.Shell.Clear();

        var result = await new WifiPreparer(adb).PrepareAsync(Usb, connectWifi: true, CancellationToken.None);

        Assert.Equal(WifiPreparationOutcome.NotResponding, result.Outcome);
        Assert.Equal(0, adb.Count("tcpip"));
    }

    [Fact]
    public async Task ConexaoRecusada_TentaAteOLimite()
    {
        var adb = Scripted();
        adb.Shell["getprop service.adb.tcp.port"] = "5555";
        adb.Connect = (host, port) => new AdbConnectResult(false, $"failed to connect to {host}:{port}");
        var time = new TimerTrackingTimeProvider();

        var prepare = new WifiPreparer(adb, timeProvider: time, connectAttempts: 3).PrepareAsync(Usb, true, CancellationToken.None);
        await time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromSeconds(1));
        await time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromSeconds(1));
        var result = await prepare.WaitAsync(TestTimeouts.Real);

        Assert.Equal(WifiPreparationOutcome.ConnectFailed, result.Outcome);
        Assert.Equal(3, adb.Count("connect"));
    }

    [Fact]
    public async Task Catalogo_GravaSoQuandoMuda()
    {
        var store = new CountingStore();
        var catalog = new PhoneCatalog(new WireflexConfig(), store);

        Assert.True(await catalog.LearnAsync(Usb, model: "Test Phone"));
        Assert.False(await catalog.LearnAsync(Usb, model: "Test Phone"));
        Assert.True(await catalog.LearnAsync(Usb, ip: "192.0.2.10"));
        Assert.False(await catalog.LearnAsync(Usb, model: null, ip: null));

        Assert.Equal(2, store.Saves);
        Assert.Equal("Test Phone", catalog.DisplayName(Usb));
        Assert.Equal("192.0.2.10", catalog.Find(Usb)!.LastKnownIp);
    }

    [Fact]
    public void Catalogo_NomeDaJanela_PrefereONomeDoAparelho()
    {
        var config = new WireflexConfig();
        config.Phones.Add(new KnownPhone { Serial = Usb, FriendlyName = "Celular de Teste", Model = "Test Phone" });
        var catalog = new PhoneCatalog(config, null);

        Assert.Equal("Celular de Teste", catalog.DisplayName(Usb));
        Assert.Equal("celular ***AL02", catalog.DisplayName("TESTSERIAL02"));
    }

    private static ScriptedAdbClient Scripted()
    {
        var adb = new ScriptedAdbClient();
        adb.Shell["ip route"] = "192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.10\n";
        adb.Shell["settings get global device_name"] = "Celular de Teste\n";
        adb.Shell["getprop service.adb.tcp.port"] = "\n";
        return adb;
    }

    private sealed class CountingStore : IConfigStore
    {
        public int Saves { get; private set; }

        public Task<WireflexConfig> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WireflexConfig());

        public Task SaveAsync(WireflexConfig config, CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }
}
