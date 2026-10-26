using System.Diagnostics;
using Wireflex.Tests.Fakes;
using Wireflex.Windows;

namespace Wireflex.Tests.Windows;

/// <summary>
/// Job Object de verdade: um processo do próprio Windows (cmd.exe esperando) amarrado ao job morre quando
/// o job fecha, como acontece se o Wireflex for morto à força.
/// </summary>
public sealed class JobObjectProcessGuardTests
{
    [Fact]
    public void FecharOJob_MataOProcessoAmarrado()
    {
        using var parent = StartWaitingCmd();
        try
        {
            var guard = new JobObjectProcessGuard();
            guard.Track(parent);
            Assert.False(parent.HasExited);

            guard.Dispose();

            Assert.True(parent.WaitForExit(TestTimeouts.Real), "o processo amarrado deveria ter morrido");
        }
        finally
        {
            KillQuietly(parent);
        }
    }

    [Fact]
    public void ProcessoNaoAmarrado_ContinuaRodando()
    {
        using var free = StartWaitingCmd();
        try
        {
            using (new JobObjectProcessGuard())
            {
            }

            Assert.False(free.WaitForExit(200));
        }
        finally
        {
            KillQuietly(free);
        }
    }

    [Fact]
    public void ProcessoQueJaTerminou_NaoFalha()
    {
        using var done = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        done.WaitForExit();
        using var guard = new JobObjectProcessGuard();

        guard.Track(done);
    }

    /// <summary>cmd.exe que fica esperando (o ping vai só para o próprio PC, sem rede).</summary>
    private static Process StartWaitingCmd() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
