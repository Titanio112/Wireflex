using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Wireflex.Core.Adb;

namespace Wireflex.Tests.Fakes;

/// <summary>
/// Servidor do adb falso, em memória (sem rede). Cada conexão lê pedidos no formato do adb e responde
/// com o roteiro registrado em <see cref="On"/>. Serviço sem roteiro recebe <c>FAIL</c>.
/// </summary>
internal sealed class FakeAdbServer : IAdbServerConnector
{
    private readonly object _sync = new();
    private readonly List<(string Service, Func<FakeAdbSide, Task> Handler)> _handlers = [];
    private readonly List<string> _requests = [];
    private readonly List<FakeAdbSide> _sides = [];

    public bool Running { get; set; } = true;

    public int Connections { get; private set; }

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>
    /// Registra o roteiro de um serviço. Termina com <c>*</c> para valer como prefixo (ex: <c>host:connect:*</c>).
    /// O último registrado para o mesmo serviço vence.
    /// </summary>
    public void On(string service, Func<FakeAdbSide, Task> handler)
    {
        lock (_sync)
        {
            _handlers.Insert(0, (service, handler));
        }
    }

    /// <summary>Responde <c>OKAY</c> + texto com tamanho hex e fecha.</summary>
    public void Reply(string service, string text) => On(service, async side =>
    {
        await side.OkayAsync();
        await side.LengthPrefixedAsync(text);
        side.Close();
    });

    /// <summary>Responde <c>OKAY</c> e mantém a conexão para o próximo pedido (ex: <c>host:transport:</c>).</summary>
    public void Accept(string service) => On(service, side => side.OkayAsync());

    /// <summary>Responde <c>OKAY</c> + saída crua e fecha (ex: <c>shell:</c>).</summary>
    public void Output(string service, string output) => On(service, async side =>
    {
        await side.OkayAsync();
        await side.RawAsync(output);
        side.Close();
    });

    public void Fail(string service, string reason) => On(service, async side =>
    {
        await side.FailAsync(reason);
        side.Close();
    });

    /// <summary>Servidor caiu (ex: <c>adb kill-server</c>): fecha as conexões abertas e recusa as novas.</summary>
    public void Kill()
    {
        List<FakeAdbSide> sides;
        lock (_sync)
        {
            Running = false;
            sides = _sides.ToList();
        }

        foreach (var side in sides)
        {
            side.Close();
        }
    }

    public Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Running)
        {
            return Task.FromResult<Stream?>(null);
        }

        var (client, server) = DuplexStream.CreatePair();
        var side = new FakeAdbSide(server);
        lock (_sync)
        {
            Connections++;
            _sides.Add(side);
        }

        _ = ServeAsync(side);
        return Task.FromResult<Stream?>(client);
    }

    private async Task ServeAsync(FakeAdbSide side)
    {
        try
        {
            while (!side.Closed)
            {
                var request = await side.ReadRequestAsync();
                if (request is null)
                {
                    return;
                }

                Func<FakeAdbSide, Task>? handler;
                lock (_sync)
                {
                    _requests.Add(request);
                    handler = _handlers.FirstOrDefault(h => Matches(h.Service, request)).Handler;
                }

                if (handler is null)
                {
                    await side.FailAsync($"unknown host service '{request}'");
                    side.Close();
                    return;
                }

                await handler(side);
            }
        }
        catch (IOException)
        {
            // O cliente fechou a conexão.
        }
    }

    private static bool Matches(string pattern, string request) =>
        pattern.EndsWith('*') ? request.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == request;
}

/// <summary>O lado do servidor de uma conexão.</summary>
internal sealed class FakeAdbSide(DuplexStream stream)
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Closed => _closed.Task.IsCompleted;

    /// <summary>Completa quando o cliente fecha a conexão ou o servidor a encerra.</summary>
    public Task WhenClosed => Task.WhenAny(_closed.Task, stream.RemoteClosed);

    public Task OkayAsync() => RawAsync("OKAY");

    public Task FailAsync(string reason) => RawAsync("FAIL" + Hex(reason) + reason);

    public Task LengthPrefixedAsync(string text) => RawAsync(Hex(text) + text);

    /// <summary>Uma mensagem de <c>track-devices</c>: tamanho hex + lista.</summary>
    public Task FrameAsync(string list) => LengthPrefixedAsync(list);

    public async Task RawAsync(string text) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text));

    public void Close()
    {
        _closed.TrySetResult();
        stream.Dispose();
    }

    internal async Task<string?> ReadRequestAsync()
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var length = int.Parse(Encoding.ASCII.GetString(header), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return Encoding.UTF8.GetString(body);
    }

    private static string Hex(string text) =>
        Encoding.UTF8.GetByteCount(text).ToString("x4", CultureInfo.InvariantCulture);
}

/// <summary>Sobe o servidor falso quando o cliente pede <c>adb start-server</c>.</summary>
internal sealed class FakeAdbServerLauncher(FakeAdbServer server) : IAdbServerLauncher
{
    public int? ClientVersion { get; set; } = 41;

    public int StartServerCalls { get; private set; }

    public int VersionCalls { get; private set; }

    /// <summary>O que acontece no <c>start-server</c>. Padrão: o servidor passa a rodar.</summary>
    public Action OnStartServer { get; set; } = () => { };

    public Task<int?> GetClientVersionAsync(CancellationToken cancellationToken)
    {
        VersionCalls++;
        return Task.FromResult(ClientVersion);
    }

    public Task StartServerAsync(CancellationToken cancellationToken)
    {
        StartServerCalls++;
        server.Running = true;
        OnStartServer();
        return Task.CompletedTask;
    }
}

/// <summary>Par de streams ligados em memória: o que um escreve, o outro lê.</summary>
internal sealed class DuplexStream : Stream
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private readonly TaskCompletionSource _remoteClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DuplexStream? _peer;
    private byte[]? _current;
    private int _offset;

    private DuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>Completa quando o outro lado foi fechado.</summary>
    public Task RemoteClosed => _remoteClosed.Task;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public static (DuplexStream A, DuplexStream B) CreatePair()
    {
        var aToB = Channel.CreateUnbounded<byte[]>();
        var bToA = Channel.CreateUnbounded<byte[]>();
        var a = new DuplexStream(bToA, aToB);
        var b = new DuplexStream(aToB, bToA);
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current is null || _offset >= _current.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            if (_incoming.Reader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_outgoing.Writer.TryWrite(buffer.ToArray()))
        {
            throw new IOException("A conexão foi fechada.");
        }

        return ValueTask.CompletedTask;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

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
            // O outro lado lê fim de fluxo e não consegue mais escrever.
            _outgoing.Writer.TryComplete();
            _incoming.Writer.TryComplete();
            _peer?._remoteClosed.TrySetResult();
        }

        base.Dispose(disposing);
    }
}
