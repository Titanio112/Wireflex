using Wireflex.Core.Scrcpy;

namespace Wireflex.Tests.Scrcpy;

public sealed class ScrcpyPortAllocatorTests
{
    private static readonly HashSet<int> NoListeners = [];

    [Fact]
    public void Faixa_FicaForaDaPadraoDoScrcpy()
    {
        // A padrão do scrcpy (e a que a v1 usa) é 27183–27199.
        Assert.True(ScrcpyPortAllocator.FirstPort > 27199);
    }

    [Fact]
    public void NaoRepeteEntreOsAbertos()
    {
        var ports = new ScrcpyPortAllocator(() => NoListeners);

        var first = ports.Next([]);
        var second = ports.Next([first!.Value]);

        Assert.Equal(ScrcpyPortAllocator.FirstPort, first);
        Assert.NotEqual(first, second);
        Assert.InRange(second!.Value, ScrcpyPortAllocator.FirstPort, ScrcpyPortAllocator.LastPort);
    }

    [Fact]
    public void PulaPortaComAlguemEscutando()
    {
        var ports = new ScrcpyPortAllocator(() => new HashSet<int> { 27200, 27201 });

        Assert.Equal(27202, ports.Next([]));
    }

    [Fact]
    public void PortaLiberada_SoVoltaDepoisDasOutras()
    {
        var ports = new ScrcpyPortAllocator(() => NoListeners);

        var first = ports.Next([]);
        var second = ports.Next([]);

        // O primeiro scrcpy fechou: a porta dele não é a próxima.
        Assert.Equal(first + 1, second);
    }

    [Fact]
    public void DepoisDaUltima_VoltaParaOComeco()
    {
        var ports = new ScrcpyPortAllocator(() => NoListeners);
        var taken = Enumerable.Range(ScrcpyPortAllocator.FirstPort, ScrcpyPortAllocator.LastPort - ScrcpyPortAllocator.FirstPort).ToList();

        Assert.Equal(ScrcpyPortAllocator.LastPort, ports.Next(taken));
        Assert.Equal(ScrcpyPortAllocator.FirstPort, ports.Next([]));
    }

    [Fact]
    public void FaixaToda_Ocupada_UsaAPadrao()
    {
        var all = Enumerable.Range(ScrcpyPortAllocator.FirstPort, ScrcpyPortAllocator.LastPort - ScrcpyPortAllocator.FirstPort + 1).ToHashSet();
        var ports = new ScrcpyPortAllocator(() => all);

        Assert.Null(ports.Next([]));
    }
}
