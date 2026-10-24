using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Logging;

namespace Wireflex.Core.Adb;

/// <summary>
/// <see cref="IAdbClient"/> real: chama o adb.exe como processo. Só comandos de leitura.
/// </summary>
public sealed class AdbClient : IAdbClient
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SerialTimeout = TimeSpan.FromSeconds(5);
    private const int MaxStderrLines = 20;

    private readonly ILogger<AdbClient> _logger;

    public AdbClient(string adbPath, ILogger<AdbClient>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        AdbPath = adbPath;
        _logger = logger ?? NullLogger<AdbClient>.Instance;
    }

    public string AdbPath { get; }

    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["devices", "-l"], ListTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new AdbException($"adb devices falhou (código {result.ExitCode}): {Sensitive.ScrubIpAddresses(result.StdErr.Trim())}");
        }

        return AdbDeviceListParser.Parse(result.StdOut);
    }

    public async IAsyncEnumerable<IReadOnlyList<AdbDevice>> TrackDevicesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var process = Start(["track-devices", "-l"]);
        var stderr = new BoundedLines(MaxStderrLines);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.Add(e.Data);
            }
        };
        process.BeginErrorReadLine();

        _logger.LogInformation("adb track-devices iniciado (pid {Pid})", process.Id);

        using var registration = cancellationToken.Register(() => TryKill(process));
        var decoder = new TrackDevicesFrameDecoder();
        var buffer = new byte[4096];
        var stream = process.StandardOutput.BaseStream;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                foreach (var payload in decoder.Append(buffer.AsSpan(0, read)))
                {
                    yield return AdbDeviceListParser.Parse(payload);
                }
            }
        }
        finally
        {
            TryKill(process);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogWarning(
            "adb track-devices terminou (código {ExitCode}). stderr: {Stderr}",
            process.ExitCode,
            Sensitive.ScrubIpAddresses(stderr.ToString()));
    }

    public async Task<string?> GetSerialNumberAsync(string adbSerial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbSerial);

        try
        {
            var result = await RunAsync(["-s", adbSerial, "shell", "getprop", "ro.serialno"], SerialTimeout, cancellationToken)
                .ConfigureAwait(false);
            var serial = result.StdOut.Trim();
            if (result.ExitCode == 0 && serial.Length > 0)
            {
                return serial;
            }

            _logger.LogDebug("Não foi possível ler o serial de {Connection} (código {ExitCode})",
                Sensitive.MaskAdbSerial(adbSerial), result.ExitCode);
        }
        catch (AdbException ex)
        {
            _logger.LogDebug(ex, "Não foi possível ler o serial de {Connection}", Sensitive.MaskAdbSerial(adbSerial));
        }

        return null;
    }

    private Process Start(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo) ?? throw new AdbException("Não foi possível iniciar o adb.");
        }
        catch (Win32Exception ex)
        {
            throw new AdbException("Não foi possível iniciar o adb. Confira o caminho configurado.", ex);
        }
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Start(arguments);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new AdbException($"adb {arguments[0]} não respondeu em {timeout.TotalSeconds:0} s.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Processo já terminou.
        }
        catch (Win32Exception)
        {
            // Sem permissão ou já terminando; nada a fazer.
        }
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

    private sealed class BoundedLines(int capacity)
    {
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > capacity)
                {
                    _lines.Dequeue();
                }
            }
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return string.Join(" | ", _lines);
            }
        }
    }
}
