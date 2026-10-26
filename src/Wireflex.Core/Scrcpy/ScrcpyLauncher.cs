using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Logging;
using Wireflex.Core.Processes;

namespace Wireflex.Core.Scrcpy;

/// <summary>
/// Abre o scrcpy.exe de verdade: <c>--serial</c> com a conexão escolhida, título da janela e a variável
/// <c>ADB</c> apontando para o mesmo adb do Wireflex (o zip do scrcpy traz outro adb, que pode ser de outra
/// versão e derrubar o servidor). A saída vai para o log, com serial e IP mascarados.
/// </summary>
public sealed class ScrcpyLauncher : IScrcpyLauncher
{
    private readonly string _scrcpyPath;
    private readonly string? _adbPath;
    private readonly IChildProcessGuard _guard;
    private readonly ILogger<ScrcpyLauncher> _logger;

    public ScrcpyLauncher(string scrcpyPath, string? adbPath, IChildProcessGuard? guard = null, ILogger<ScrcpyLauncher>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scrcpyPath);
        _scrcpyPath = scrcpyPath;
        _adbPath = adbPath;
        _guard = guard ?? NoChildProcessGuard.Instance;
        _logger = logger ?? NullLogger<ScrcpyLauncher>.Instance;
    }

    /// <summary>Argumentos passados ao scrcpy (sem o executável).</summary>
    public static IReadOnlyList<string> BuildArguments(ScrcpyLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = ["--serial", request.AdbSerial, "--window-title", request.WindowTitle];
        if (request.Port is { } port)
        {
            arguments.Add(FormattableString.Invariant($"--port={port}"));
        }

        return arguments;
    }

    public IScrcpyProcess Start(ScrcpyLaunchRequest request)
    {
        var startInfo = new ProcessStartInfo(_scrcpyPath)
        {
            UseShellExecute = false,

            // Esconde só o console do scrcpy; a janela do espelhamento abre normalmente.
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in BuildArguments(request))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (_adbPath is not null)
        {
            startInfo.Environment["ADB"] = _adbPath;
        }

        // Quem escolhe o aparelho é o --serial.
        startInfo.Environment.Remove("ANDROID_SERIAL");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pid = 0;
        process.Exited += (_, _) => exited.TrySetResult(SafeExitCode(process));
        process.OutputDataReceived += (_, e) => LogOutput(pid, request, e.Data);
        process.ErrorDataReceived += (_, e) => LogOutput(pid, request, e.Data);

        try
        {
            if (!process.Start())
            {
                throw new ScrcpyException("Não foi possível iniciar o scrcpy.");
            }
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new ScrcpyException("Não foi possível iniciar o scrcpy. Confira o caminho configurado.", ex);
        }

        pid = process.Id;
        _guard.Track(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation(
            "scrcpy aberto (pid {Pid}) para {Phone} via {Connection}. Argumentos: {Arguments}",
            process.Id,
            Sensitive.Mask(request.PhoneSerial),
            Sensitive.MaskAdbSerial(request.AdbSerial),
            string.Join(' ', BuildArguments(request with { WindowTitle = request.SafeWindowTitle })
                .Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)
                .Select(a => Sensitive.ScrubKnown(a, request.PhoneSerial, request.AdbSerial))));

        // O Exited não depende do fim da saída redirecionada (um adb filho do scrcpy pode mantê-la aberta).
        _ = exited.Task.ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        return new RunningScrcpy(process, exited.Task);
    }

    private void LogOutput(int pid, ScrcpyLaunchRequest request, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Com o pid: a linha "Device: ..." diz a qual celular este scrcpy se conectou de fato.
        _logger.LogInformation("scrcpy[{Pid}] {Phone}: {Line}", pid, Sensitive.Mask(request.PhoneSerial),
            Sensitive.ScrubKnown(line, request.PhoneSerial, request.AdbSerial));
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private sealed class RunningScrcpy(Process process, Task<int> exited) : IScrcpyProcess
    {
        public int Id { get; } = process.Id;

        public Task<int> Exited => exited;

        public string ReadWindowTitle()
        {
            if (exited.IsCompleted)
            {
                return string.Empty;
            }

            try
            {
                process.Refresh();
                return process.MainWindowTitle;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return string.Empty;
            }
        }

        public void Kill()
        {
            if (exited.IsCompleted)
            {
                return;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Já terminou ou está terminando.
            }
        }
    }
}
