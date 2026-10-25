using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wireflex.Core.Adb;

/// <summary>Abre conexões com o servidor do adb. Os testes usam uma versão em memória.</summary>
public interface IAdbServerConnector
{
    /// <summary>Abre uma conexão nova. Retorna <c>null</c> se o servidor não estiver rodando.</summary>
    Task<Stream?> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Sobe o servidor do adb e informa a versão do adb.exe.</summary>
public interface IAdbServerLauncher
{
    /// <summary>Versão do protocolo do adb.exe (ex: 41 em "1.0.41"); <c>null</c> se não der para saber.</summary>
    Task<int?> GetClientVersionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Roda <c>adb start-server</c>. Se o servidor rodando for de outra versão, o próprio adb o reinicia.
    /// </summary>
    Task StartServerAsync(CancellationToken cancellationToken);
}

/// <summary>Conexão TCP com o servidor do adb em <c>127.0.0.1</c>.</summary>
public sealed class TcpAdbServerConnector(int port = TcpAdbServerConnector.DefaultPort) : IAdbServerConnector
{
    public const int DefaultPort = 5037;

    /// <summary>Porta de <c>ANDROID_ADB_SERVER_PORT</c> (a mesma variável que o adb usa) ou 5037.</summary>
    public static TcpAdbServerConnector FromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT");
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
            ? new TcpAdbServerConnector(port)
            : new TcpAdbServerConnector();
    }

    public int Port { get; } = port;

    public async Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            socket.Dispose();
            return null;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>Usa o adb.exe só para subir o servidor e saber a versão. Todo o resto vai pelo socket.</summary>
public sealed partial class AdbServerLauncher : IAdbServerLauncher
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly string _adbPath;
    private readonly ILogger _logger;

    public AdbServerLauncher(string adbPath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        _adbPath = adbPath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Extrai a versão do protocolo de <c>adb version</c> ("Android Debug Bridge version 1.0.41" = 41).</summary>
    public static int? ParseClientVersion(string output)
    {
        var match = ClientVersionRegex().Match(output ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    public async Task<int?> GetClientVersionAsync(CancellationToken cancellationToken)
    {
        // "adb version" não sobe o servidor, então pode ter a saída redirecionada sem herança de pipes presa.
        var startInfo = new ProcessStartInfo(_adbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("version");

        Process? process = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(VersionTimeout);
        try
        {
            process = Process.Start(startInfo) ?? throw new AdbException("Não foi possível iniciar o adb.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return ParseClientVersion(await stdout.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is Win32Exception or AdbException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Não foi possível ler a versão do adb.exe");
            return null;
        }
        finally
        {
            if (process is not null)
            {
                KillQuietly(process);
                process.Dispose();
            }
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Já terminou ou está terminando.
        }
    }

    public async Task StartServerAsync(CancellationToken cancellationToken)
    {
        // O servidor nasce como filho deste adb e herdaria os handles herdáveis (pipes) de quem o criou.
        // No Windows, ShellExecute cria o processo sem herança; a janela fica escondida.
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(_adbPath, "start-server") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }
            : new ProcessStartInfo(_adbPath, "start-server") { UseShellExecute = false, CreateNoWindow = true };

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new AdbException("Não foi possível iniciar o adb.");
        }
        catch (Win32Exception ex)
        {
            throw new AdbException("Não foi possível iniciar o adb. Confira o caminho configurado.", ex);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartTimeout);
            try
            {
                // Espera só o adb cliente; o servidor continua rodando sozinho.
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AdbException($"adb start-server não terminou em {StartTimeout.TotalSeconds:0} s.");
            }

            if (process.ExitCode != 0)
            {
                throw new AdbException($"adb start-server falhou (código {process.ExitCode}).");
            }
        }

        _logger.LogInformation("Servidor do adb iniciado (adb start-server)");
    }

    [GeneratedRegex(@"Android Debug Bridge version \d+\.\d+\.(\d+)")]
    private static partial Regex ClientVersionRegex();
}
