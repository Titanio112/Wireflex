using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Logging;

namespace Wireflex.Core.Adb;

/// <summary>
/// <see cref="IAdbClient"/> real: fala direto com o servidor do adb pelo socket (porta 5037).
/// O adb.exe só é chamado para subir o servidor (<see cref="IAdbServerLauncher"/>).
/// </summary>
/// <remarks>
/// Pelo socket não há processo filho para ficar órfão nem a troca de <c>\n</c> por <c>\r\n</c>
/// que o adb.exe faz na saída padrão do Windows.
/// </remarks>
public sealed class AdbClient : IAdbClient
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ServerStartPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ServerStartPollLimit = TimeSpan.FromSeconds(3);

    private readonly IAdbServerConnector _connector;
    private readonly IAdbServerLauncher _launcher;
    private readonly ILogger<AdbClient> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private bool _versionChecked;

    public AdbClient(string adbPath, ILogger<AdbClient>? logger = null)
        : this(TcpAdbServerConnector.FromEnvironment(), new AdbServerLauncher(adbPath, logger), logger)
    {
    }

    public AdbClient(
        IAdbServerConnector connector,
        IAdbServerLauncher launcher,
        ILogger<AdbClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? NullLogger<AdbClient>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        WithTimeoutAsync(CommandTimeout, "host:devices-l", async token =>
        {
            await using var stream = await OpenAsync("host:devices-l", token).ConfigureAwait(false);
            return AdbDeviceListParser.Parse(await AdbWire.ReadLengthPrefixedAsync(stream, token).ConfigureAwait(false));
        }, cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<AdbDevice>> TrackDevicesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureServerAsync(cancellationToken).ConfigureAwait(false);

        await using var stream = await OpenAsync("host:track-devices-l", cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Acompanhando o servidor do adb (host:track-devices-l)");

        var decoder = new TrackDevicesFrameDecoder();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await ReadOrEndAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            foreach (var payload in decoder.Append(buffer.AsSpan(0, read)))
            {
                yield return AdbDeviceListParser.Parse(payload);
            }
        }

        _logger.LogWarning("O servidor do adb encerrou o acompanhamento (caiu ou reiniciou)");
    }

    /// <summary>Conexão derrubada pelo servidor (ex: <c>adb kill-server</c>) é fim do fluxo, não erro.</summary>
    private async Task<int> ReadOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Conexão com o servidor do adb caiu");
            return 0;
        }
    }

    public async Task<string?> GetSerialNumberAsync(string adbSerial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbSerial);

        try
        {
            var serial = (await ShellAsync(adbSerial, "getprop ro.serialno", cancellationToken).ConfigureAwait(false)).Trim();
            if (serial.Length > 0)
            {
                return serial;
            }

            _logger.LogDebug("{Connection} não informou o serial", Sensitive.MaskAdbSerial(adbSerial));
        }
        catch (Exception ex) when (ex is AdbException or IOException)
        {
            _logger.LogDebug(ex, "Não foi possível ler o serial de {Connection}", Sensitive.MaskAdbSerial(adbSerial));
        }

        return null;
    }

    public Task<string> ShellAsync(string adbSerial, string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return WithTimeoutAsync(CommandTimeout, "shell", async token =>
        {
            await using var stream = await OpenOnDeviceAsync(adbSerial, "shell:" + command, token).ConfigureAwait(false);
            return await AdbWire.ReadToEndAsync(stream, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<string> EnableTcpipAsync(string adbSerial, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbSerial);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        return WithTimeoutAsync(CommandTimeout, "tcpip", async token =>
        {
            var service = "tcpip:" + port.ToString(CultureInfo.InvariantCulture);
            await using var stream = await OpenOnDeviceAsync(adbSerial, service, token).ConfigureAwait(false);
            try
            {
                return (await AdbWire.ReadToEndAsync(stream, token).ConfigureAwait(false)).Trim();
            }
            catch (IOException)
            {
                // O adbd reinicia logo depois de responder; a conexão pode cair antes do fim da mensagem.
                return string.Empty;
            }
        }, cancellationToken);
    }

    public Task<AdbConnectResult> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        return WithTimeoutAsync(ConnectTimeout, "host:connect", async token =>
        {
            var service = $"host:connect:{host}:{port.ToString(CultureInfo.InvariantCulture)}";
            await using var stream = await OpenAsync(service, token).ConfigureAwait(false);
            var message = (await AdbWire.ReadLengthPrefixedAsync(stream, token).ConfigureAwait(false)).Trim();

            // O adb responde OKAY mesmo quando não conecta; o resultado está no texto.
            var connected = message.StartsWith("connected to", StringComparison.Ordinal)
                || message.StartsWith("already connected to", StringComparison.Ordinal);
            return new AdbConnectResult(connected, message);
        }, cancellationToken);
    }

    public Task<bool> DisconnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        return WithTimeoutAsync(CommandTimeout, "host:disconnect", async token =>
        {
            var service = $"host:disconnect:{host}:{port.ToString(CultureInfo.InvariantCulture)}";
            try
            {
                await using var stream = await OpenAsync(service, token).ConfigureAwait(false);
                await AdbWire.ReadLengthPrefixedAsync(stream, token).ConfigureAwait(false);
                return true;
            }
            catch (AdbException ex) when (ex.Message.Contains("no such device", StringComparison.OrdinalIgnoreCase))
            {
                // O servidor responde FAIL "no such device" quando não tinha essa conexão.
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Garante o servidor do adb no ar. Se não estiver, roda <c>adb start-server</c>. Na primeira vez também
    /// compara a versão do servidor com a do adb.exe: se forem diferentes, o <c>start-server</c> faz o próprio
    /// adb reiniciar o servidor. Só uma vez, para não brigar com outra ferramenta que use outro adb.
    /// </summary>
    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var serverVersion = await TryGetServerVersionAsync(cancellationToken).ConfigureAwait(false);
            if (serverVersion is null)
            {
                _logger.LogInformation("O servidor do adb não está rodando; subindo com adb start-server");
                var started = _time.GetTimestamp();
                await _launcher.StartServerAsync(cancellationToken).ConfigureAwait(false);
                serverVersion = await WaitForServerAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new AdbException("O servidor do adb não respondeu depois do adb start-server.");
                _logger.LogInformation(
                    "Servidor do adb respondendo {Seconds:0.000} s depois do start-server",
                    _time.GetElapsedTime(started).TotalSeconds);
            }

            if (_versionChecked)
            {
                return;
            }

            _versionChecked = true;
            var clientVersion = await _launcher.GetClientVersionAsync(cancellationToken).ConfigureAwait(false);
            if (clientVersion is null || clientVersion == serverVersion)
            {
                return;
            }

            _logger.LogWarning(
                "O servidor do adb está na versão {Server} e o adb.exe na {Client} (outro adb no PC?). Reiniciando o servidor uma vez",
                serverVersion,
                clientVersion);
            await _launcher.StartServerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    /// <summary>
    /// Depois do <c>start-server</c>: o servidor pode levar um instante para aceitar conexões. Tenta de novo logo,
    /// em passos curtos e fixos (sem espera crescente), por até <see cref="ServerStartPollLimit"/>.
    /// </summary>
    private async Task<int?> WaitForServerAsync(CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        while (true)
        {
            if (await TryGetServerVersionAsync(cancellationToken).ConfigureAwait(false) is { } version)
            {
                return version;
            }

            if (_time.GetElapsedTime(started) >= ServerStartPollLimit)
            {
                return null;
            }

            await Task.Delay(ServerStartPollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int?> TryGetServerVersionAsync(CancellationToken cancellationToken)
    {
        var stream = await _connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            const string service = "host:version";
            await AdbWire.SendRequestAsync(stream, service, cancellationToken).ConfigureAwait(false);
            await AdbWire.ReadStatusAsync(stream, service, cancellationToken).ConfigureAwait(false);
            var text = await AdbWire.ReadLengthPrefixedAsync(stream, cancellationToken).ConfigureAwait(false);
            return int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var version)
                ? version
                : throw new AdbException($"Versão inesperada do servidor do adb ('{text}').");
        }
    }

    /// <summary>Abre uma conexão e pede um serviço do servidor. Não sobe o servidor.</summary>
    private async Task<Stream> OpenAsync(string service, CancellationToken cancellationToken)
    {
        var stream = await _connector.ConnectAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new AdbException("O servidor do adb não está rodando.");
        try
        {
            await AdbWire.SendRequestAsync(stream, service, cancellationToken).ConfigureAwait(false);
            await AdbWire.ReadStatusAsync(stream, service, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Escolhe o aparelho (<c>host:transport:</c>) e pede um serviço dele (ex: <c>shell:</c>).</summary>
    private async Task<Stream> OpenOnDeviceAsync(string adbSerial, string service, CancellationToken cancellationToken)
    {
        var stream = await OpenAsync("host:transport:" + adbSerial, cancellationToken).ConfigureAwait(false);
        try
        {
            await AdbWire.SendRequestAsync(stream, service, cancellationToken).ConfigureAwait(false);
            await AdbWire.ReadStatusAsync(stream, service, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<T> WithTimeoutAsync<T>(
        TimeSpan timeout,
        string what,
        Func<CancellationToken, Task<T>> run,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            return await run(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AdbException($"O adb não respondeu a '{what}' em {timeout.TotalSeconds:0} s.");
        }
    }
}
