using Wireflex.Windows;

namespace Wireflex.Tests.Windows;

/// <summary>Mutex e evento reais do Windows, com um nome único por teste.</summary>
public sealed class SingleInstanceTests
{
    private readonly string _name = "Wireflex.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void SegundaInstancia_NaoConsegue()
    {
        using var first = SingleInstance.TryAcquire(_name);

        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(_name));
    }

    [Fact]
    public void DepoisDeAPrimeiraFechar_OutraConsegue()
    {
        SingleInstance.TryAcquire(_name)!.Dispose();

        using var next = SingleInstance.TryAcquire(_name);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task SegundaExecucao_AvisaAPrimeira()
    {
        using var first = SingleInstance.TryAcquire(_name)!;
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += () => activated.TrySetResult();

        Assert.True(SingleInstance.SignalExisting(_name));

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void SemInstanciaRodando_AvisoNaoTemQuemOuvir()
    {
        Assert.False(SingleInstance.SignalExisting(_name));
    }
}
