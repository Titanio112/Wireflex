using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Wireflex.Core.Adb;

namespace Wireflex.Tests.Fakes;

/// <summary>
/// <see cref="IAdbClient"/> falso. Cada chamada a <see cref="TrackDevicesAsync"/> consome a próxima
/// <see cref="FakeTrackSession"/> adicionada pelo teste (ou espera até haver uma).
/// </summary>
internal sealed class FakeAdbClient(TimeProvider time) : IAdbClient
{
    private readonly Channel<FakeTrackSession> _sessions = Channel.CreateUnbounded<FakeTrackSession>();
    private readonly List<DateTimeOffset> _trackCalls = [];
    private int _serialLookups;

    /// <summary>Resposta de <see cref="GetSerialNumberAsync"/>. Padrão: não identifica.</summary>
    public Func<string, string?> SerialLookup { get; set; } = _ => null;

    public int SerialLookups => Volatile.Read(ref _serialLookups);

    public IReadOnlyList<DateTimeOffset> TrackCalls
    {
        get
        {
            lock (_trackCalls)
            {
                return _trackCalls.ToList();
            }
        }
    }

    public FakeTrackSession AddSession()
    {
        var session = new FakeTrackSession();
        _sessions.Writer.TryWrite(session);
        return session;
    }

    public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<IReadOnlyList<AdbDevice>> TrackDevicesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (_trackCalls)
        {
            _trackCalls.Add(time.GetUtcNow());
        }

        var session = await _sessions.Reader.ReadAsync(cancellationToken);
        session.Attach(cancellationToken);

        while (true)
        {
            var next = await session.NextAsync(cancellationToken);
            if (next is null)
            {
                yield break;
            }

            yield return next;
        }
    }

    public Task<string?> GetSerialNumberAsync(string adbSerial, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _serialLookups);
        return Task.FromResult(SerialLookup(adbSerial));
    }

    /// <summary>
    /// Para checagens negativas ("não deve consultar"): espera no máximo <paramref name="within"/> (curto)
    /// para ver se o número de consultas passa de <paramref name="count"/>.
    /// </summary>
    public async Task<bool> SerialLookupsExceedAsync(int count, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (SerialLookups > count)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return SerialLookups > count;
    }
}

/// <summary>Um "track-devices": o teste empurra fotos e depois encerra ou faz falhar.</summary>
internal sealed class FakeTrackSession
{
    private readonly object _sync = new();
    private readonly Queue<IReadOnlyList<AdbDevice>> _pending = new();
    private TaskCompletionSource _available = NewSignal();
    private TaskCompletionSource _idle = NewSignal();
    private bool _ended;
    private Exception? _failure;

    private readonly TaskCompletionSource _attached = NewSignal();

    public CancellationToken Token { get; private set; }

    /// <summary>Completa quando o monitor começa a ler esta sessão.</summary>
    public Task Attached => _attached.Task;

    public void Push(params AdbDevice[] devices)
    {
        lock (_sync)
        {
            _pending.Enqueue(devices);
            if (_idle.Task.IsCompleted)
            {
                _idle = NewSignal();
            }

            _available.TrySetResult();
        }
    }

    public void End()
    {
        lock (_sync)
        {
            _ended = true;
            _available.TrySetResult();
        }
    }

    public void Fail(Exception exception)
    {
        lock (_sync)
        {
            _failure = exception;
            _available.TrySetResult();
        }
    }

    /// <summary>
    /// Completa quando o monitor já pegou todas as fotos empurradas e pediu a próxima, ou seja,
    /// a última foto já está na fila interna do monitor.
    /// </summary>
    public Task Idle
    {
        get
        {
            lock (_sync)
            {
                return _idle.Task;
            }
        }
    }

    internal void Attach(CancellationToken token)
    {
        Token = token;
        _attached.TrySetResult();
    }

    internal async Task<IReadOnlyList<AdbDevice>?> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (_pending.TryDequeue(out var snapshot))
                {
                    return snapshot;
                }

                if (_failure is not null)
                {
                    throw _failure;
                }

                if (_ended)
                {
                    return null;
                }

                _idle.TrySetResult();
                _available = NewSignal();
                wait = _available.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal static class Devices
{
    public static AdbDevice Usb(string serial, AdbDeviceState state = AdbDeviceState.Device) =>
        new(serial, state, AdbTransport.Usb, "Test_Phone", UsbPath: "usb:1-1");

    public static AdbDevice Tcpip(string hostPort, AdbDeviceState state = AdbDeviceState.Device) =>
        new(hostPort, state, AdbTransport.WifiTcpip, "Test_Phone");

    public static AdbDevice Mdns(string serial, AdbDeviceState state = AdbDeviceState.Device) =>
        new($"adb-{serial}-AbCdEf._adb-tls-connect._tcp", state, AdbTransport.WifiMdns, "Test_Phone");
}
