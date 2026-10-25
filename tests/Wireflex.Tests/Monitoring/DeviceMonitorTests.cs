using Wireflex.Core.Adb;
using Wireflex.Core.Decisions;
using Wireflex.Core.Monitoring;
using Wireflex.Tests.Fakes;

namespace Wireflex.Tests.Monitoring;

/// <summary>
/// Monitor com adb e relógio falsos. O tempo só anda quando o teste manda, e só depois de o monitor
/// criar o timer esperado; não há espera real no caminho feliz.
/// </summary>
public sealed class DeviceMonitorTests : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan IdentityRetry = TimeSpan.FromSeconds(10);

    private readonly TimerTrackingTimeProvider _time = new();
    private readonly FakeAdbClient _adb;
    private readonly DeviceMonitor _monitor;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    public DeviceMonitorTests()
    {
        _adb = new FakeAdbClient(_time);
        _monitor = new DeviceMonitor(
            _adb,
            new DeviceMonitorOptions
            {
                Debounce = Debounce,
                InitialRetryDelay = TimeSpan.FromSeconds(1),
                MaxRetryDelay = TimeSpan.FromSeconds(4),
                IdentityRetryDelay = IdentityRetry,
            },
            timeProvider: _time);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TestTimeouts.Real);
            }
            catch (Exception)
            {
                // Testes que esperam falha já verificaram a exceção.
            }
        }

        _stop.Dispose();
    }

    private void Start() => _run = _monitor.RunAsync(_stop.Token);

    private async Task<DeviceEvent> NextEventAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Real);
        return await _monitor.Events.ReadAsync(timeout.Token);
    }

    private async Task<T> NextEventAsync<T>()
        where T : DeviceEvent => Assert.IsType<T>(await NextEventAsync());

    /// <summary>
    /// Avança o relógio em passos de <paramref name="step"/> até chegar um evento. Usado depois de
    /// <see cref="FakeTrackSession.Idle"/>, quando todas as fotos já estão na fila do monitor.
    /// </summary>
    private async Task<T> AdvanceUntilEventAsync<T>(TimeSpan step)
        where T : DeviceEvent
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Real);
        while (true)
        {
            if (_monitor.Events.TryRead(out var ready))
            {
                return Assert.IsType<T>(ready);
            }

            _time.Advance(step);
            var wait = _monitor.Events.WaitToReadAsync(timeout.Token).AsTask();
            await Task.WhenAny(wait, Task.Delay(5, timeout.Token));
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    private async Task AssertAvailableAsync(bool available)
    {
        var status = await NextEventAsync<MonitorStatusChanged>();
        Assert.Equal(available, status.AdbAvailable);
    }

    [Fact]
    public async Task FirstSnapshot_AppearedComesBeforeAdbAvailable()
    {
        var session = _adb.AddSession();
        Start();

        session.Push(Devices.Usb("TESTSERIAL01"), Devices.Usb("TESTSERIAL02", AdbDeviceState.Unauthorized));

        var first = await NextEventAsync<DeviceAppeared>();
        var second = await NextEventAsync<DeviceAppeared>();
        await AssertAvailableAsync(true);

        Assert.Equal("TESTSERIAL01", first.Connection.PhoneSerial);
        Assert.Equal(AdbTransport.Usb, first.Connection.Transport);
        Assert.Equal("Test Phone", first.Connection.Model);
        Assert.Equal(AdbDeviceState.Unauthorized, second.Connection.State);
    }

    [Fact]
    public async Task StreamEnds_RecoversWithGrowingDelay_AndResetsAfterSuccess()
    {
        var first = _adb.AddSession();
        Start();
        first.Push(Devices.Usb("TESTSERIAL01"));
        await NextEventAsync<DeviceAppeared>();
        await AssertAvailableAsync(true);

        // Servidor do adb cai.
        first.End();
        await AssertAvailableAsync(false);

        // Três tentativas falham: 1 s, 2 s, 4 s, e o teto de 4 s.
        foreach (var seconds in new[] { 1, 2, 4 })
        {
            _adb.AddSession().Fail(new AdbException("adb fora do ar"));
            await _time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromSeconds(seconds));
        }

        var back = _adb.AddSession();
        await _time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromSeconds(4));

        // Voltou com um celular novo. O que já existia não "some e reaparece".
        back.Push(Devices.Usb("TESTSERIAL01"), Devices.Usb("TESTSERIAL02"));
        var appeared = await NextEventAsync<DeviceAppeared>();
        Assert.Equal("TESTSERIAL02", appeared.Connection.PhoneSerial);
        await AssertAvailableAsync(true);

        var calls = _adb.TrackCalls;
        Assert.Equal(5, calls.Count);
        var gaps = calls.Zip(calls.Skip(1), (a, b) => b - a).ToArray();
        Assert.Equal(new[] { 1.0, 2.0, 4.0, 4.0 }, gaps.Select(g => g.TotalSeconds));

        // Depois de um sucesso a espera volta a 1 s.
        var sixth = _adb.AddSession();
        back.End();
        await AssertAvailableAsync(false);
        await _time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromSeconds(1));
        await sixth.Attached.WaitAsync(TestTimeouts.Real);
        Assert.Equal(6, _adb.TrackCalls.Count);
    }

    [Fact]
    public async Task StreamThrows_IsTreatedLikeEnd()
    {
        var first = _adb.AddSession();
        Start();
        first.Push(Devices.Usb("TESTSERIAL01"));
        await NextEventAsync<DeviceAppeared>();
        await AssertAvailableAsync(true);

        first.Fail(new AdbException("resposta inesperada"));

        var lost = await NextEventAsync<MonitorStatusChanged>();
        Assert.False(lost.AdbAvailable);
        Assert.Contains("resposta inesperada", lost.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Debounce_FlickeringAdb_ProducesNoEvents()
    {
        var session = _adb.AddSession();
        Start();
        session.Push(Devices.Usb("TESTSERIAL01"));
        await NextEventAsync<DeviceAppeared>();
        await AssertAvailableAsync(true);

        // O adb oscila: some, volta offline, volta ok. Tudo dentro do debounce.
        session.Push();
        session.Push(Devices.Usb("TESTSERIAL01", AdbDeviceState.Offline));
        session.Push(Devices.Usb("TESTSERIAL01"));
        await session.Idle;
        _time.Advance(Debounce * 2);

        // Uma mudança real depois: tem que ser o próximo evento (nada de Sumiu/Mudou de estado antes).
        session.Push(Devices.Usb("TESTSERIAL01"), Devices.Usb("TESTSERIAL02"));
        await session.Idle;

        var appeared = await AdvanceUntilEventAsync<DeviceAppeared>(Debounce);
        Assert.Equal("TESTSERIAL02", appeared.Connection.PhoneSerial);
    }

    [Fact]
    public async Task Debounce_PublishesOnlyTheLastSnapshot()
    {
        var session = _adb.AddSession();
        Start();
        session.Push(Devices.Usb("TESTSERIAL01"));
        await NextEventAsync<DeviceAppeared>();
        await AssertAvailableAsync(true);

        session.Push(Devices.Usb("TESTSERIAL01", AdbDeviceState.Offline));
        session.Push(Devices.Usb("TESTSERIAL01", AdbDeviceState.Unauthorized));
        await session.Idle;

        var changed = await AdvanceUntilEventAsync<DeviceStateChanged>(Debounce);
        Assert.Equal(AdbDeviceState.Device, changed.PreviousState);
        Assert.Equal(AdbDeviceState.Unauthorized, changed.Connection.State);
    }

    [Fact]
    public async Task CableOutWithWifi_ReportsTransportChange()
    {
        _adb.SerialLookup = _ => "TESTSERIAL01";
        var session = _adb.AddSession();
        Start();
        session.Push(Devices.Usb("TESTSERIAL01"), Devices.Tcpip("192.0.2.10:5555"));
        await NextEventAsync<DeviceAppeared>();
        await NextEventAsync<DeviceAppeared>();
        await AssertAvailableAsync(true);

        session.Push(Devices.Tcpip("192.0.2.10:5555"));
        await session.Idle;

        var gone = await AdvanceUntilEventAsync<DeviceDisappeared>(Debounce);
        Assert.Equal(AdbTransport.Usb, gone.Connection.Transport);
        var transport = await NextEventAsync<DeviceTransportChanged>();
        Assert.Equal("TESTSERIAL01", transport.PhoneKey);
        Assert.Equal(AdbTransport.Usb, transport.PreviousTransport);
        Assert.Equal(AdbTransport.WifiTcpip, transport.CurrentTransport);
    }

    [Fact]
    public async Task ConsumerFailure_StopsMonitorInsteadOfHanging()
    {
        _adb.SerialLookup = _ => throw new InvalidOperationException("falha inesperada ao identificar");
        var session = _adb.AddSession();
        Start();

        session.Push(Devices.Tcpip("192.0.2.10:5555"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _run!.WaitAsync(TestTimeouts.Real));
        Assert.Contains("identificar", ex.Message, StringComparison.Ordinal);
        Assert.True(session.Token.IsCancellationRequested, "o track-devices deveria ter sido cancelado");
        await _monitor.Events.Completion.WaitAsync(TestTimeouts.Real);
    }

    [Fact]
    public async Task Cancellation_StopsCleanly()
    {
        var session = _adb.AddSession();
        Start();
        session.Push(Devices.Usb("TESTSERIAL01"));
        await NextEventAsync<DeviceAppeared>();
        await AssertAvailableAsync(true);

        await _stop.CancelAsync();

        await _run!.WaitAsync(TestTimeouts.Real);
        Assert.True(session.Token.IsCancellationRequested);
        await _monitor.Events.Completion.WaitAsync(TestTimeouts.Real);
    }

    [Fact]
    public async Task WifiIdentifiedByGetprop()
    {
        _adb.SerialLookup = adbSerial => adbSerial == "192.0.2.10:5555" ? "TESTSERIAL01" : null;
        var session = _adb.AddSession();
        Start();

        session.Push(Devices.Tcpip("192.0.2.10:5555"));

        var appeared = await NextEventAsync<DeviceAppeared>();
        Assert.Equal("TESTSERIAL01", appeared.Connection.PhoneSerial);
        Assert.Equal("TESTSERIAL01", appeared.Connection.Key);
    }

    [Fact]
    public async Task MdnsIdentifiedByName_WhenGetpropFails()
    {
        var session = _adb.AddSession();
        Start();

        session.Push(Devices.Mdns("TESTSERIAL02"));

        var appeared = await NextEventAsync<DeviceAppeared>();
        Assert.Equal("TESTSERIAL02", appeared.Connection.PhoneSerial);
        Assert.Equal(AdbTransport.WifiMdns, appeared.Connection.Transport);
    }

    [Fact]
    public async Task UnidentifiedWifi_RetriesIdentification_ThenAppearsWithSerial()
    {
        var answers = new Queue<string?>(new[] { null, "TESTSERIAL01" });
        _adb.SerialLookup = _ => answers.Count > 0 ? answers.Dequeue() : "TESTSERIAL01";
        var session = _adb.AddSession();
        Start();

        session.Push(Devices.Tcpip("192.0.2.10:5555"));

        var unidentified = await NextEventAsync<DeviceAppeared>();
        Assert.Null(unidentified.Connection.PhoneSerial);
        Assert.Null(MonitorEventTranslator.Translate(unidentified));
        await AssertAvailableAsync(true);

        await _time.AdvanceWhenTimerCreatedAsync(IdentityRetry);

        var identified = await NextEventAsync<DeviceAppeared>();
        Assert.Equal("TESTSERIAL01", identified.Connection.PhoneSerial);
        Assert.IsType<ConnectionAdded>(MonitorEventTranslator.Translate(identified));

        var oldKey = await NextEventAsync<DeviceDisappeared>();
        Assert.Null(oldKey.Connection.PhoneSerial);
        Assert.Null(MonitorEventTranslator.Translate(oldKey));
        Assert.Equal(2, _adb.SerialLookups);
    }

    [Fact]
    public async Task UnidentifiedWifi_DoesNotRetryWhileAdbIsDown()
    {
        var session = _adb.AddSession();
        Start();
        session.Push(Devices.Tcpip("192.0.2.10:5555"));
        await NextEventAsync<DeviceAppeared>();
        await AssertAvailableAsync(true);
        Assert.Equal(1, _adb.SerialLookups);

        // adb cai e não volta (nenhuma sessão nova). Passa bem mais que o intervalo de nova identificação.
        session.End();
        await AssertAvailableAsync(false);
        await _time.AdvanceWhenTimerCreatedAsync(TimeSpan.FromSeconds(1));
        _time.Advance(IdentityRetry * 3);

        // Com o adb fora, rodar "adb -s ... shell getprop" subiria o servidor por conta própria.
        Assert.False(await _adb.SerialLookupsExceedAsync(1, TimeSpan.FromMilliseconds(300)));
    }
}
