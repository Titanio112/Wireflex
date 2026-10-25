using Wireflex.Core.Adb;

namespace Wireflex.Tests.Fakes;

/// <summary>
/// adb falso para os comandos de conexão (shell, tcpip, connect). Responde na hora, com o roteiro do teste,
/// e registra cada chamada. O acompanhamento de aparelhos não é usado aqui.
/// </summary>
internal sealed class ScriptedAdbClient : IAdbClient
{
    private readonly object _sync = new();
    private readonly List<string> _calls = [];

    /// <summary>Saída de cada comando do shell. Sem entrada: lança <see cref="AdbException"/>.</summary>
    public Dictionary<string, string> Shell { get; } = new(StringComparer.Ordinal);

    /// <summary>Resposta do connect. Padrão: conecta.</summary>
    public Func<string, int, AdbConnectResult> Connect { get; set; } =
        (host, port) => new AdbConnectResult(true, $"connected to {host}:{port}");

    /// <summary>Chamado no tcpip (ex: para o teste simular a conexão USB caindo).</summary>
    public Action<string>? OnTcpip { get; set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_sync)
            {
                return _calls.ToList();
            }
        }
    }

    public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

    public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<IReadOnlyList<AdbDevice>> TrackDevicesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<string?> GetSerialNumberAsync(string adbSerial, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<string> ShellAsync(string adbSerial, string command, CancellationToken cancellationToken = default)
    {
        Record($"shell {adbSerial} {command}");
        return Shell.TryGetValue(command, out var output)
            ? Task.FromResult(output)
            : Task.FromException<string>(new AdbException($"comando sem roteiro: {command}"));
    }

    public Task<string> EnableTcpipAsync(string adbSerial, int port, CancellationToken cancellationToken = default)
    {
        Record($"tcpip {adbSerial} {port}");
        OnTcpip?.Invoke(adbSerial);
        return Task.FromResult($"restarting in TCP mode port: {port}");
    }

    /// <summary>O connect fica preso até ser cancelado (como com um IP errado na rede).</summary>
    public bool ConnectHangs { get; set; }

    public async Task<AdbConnectResult> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        Record($"connect {host}:{port}");
        if (ConnectHangs)
        {
            // Sem o contexto do xUnit: o cancelamento continua na hora, como no adb real.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        return Connect(host, port);
    }

    private void Record(string call)
    {
        lock (_sync)
        {
            _calls.Add(call);
        }
    }
}
