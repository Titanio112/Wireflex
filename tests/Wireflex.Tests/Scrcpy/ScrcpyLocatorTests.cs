using Wireflex.Core.Scrcpy;

namespace Wireflex.Tests.Scrcpy;

/// <summary>Busca do scrcpy com sistema de arquivos falso (só caminhos fictícios).</summary>
public class ScrcpyLocatorTests
{
    private static readonly string Tools = Path.Combine("X:", "Ferramentas", "scrcpy");
    private static readonly string Exe = Path.Combine(Tools, ScrcpyLocator.ExecutableName);

    [Fact]
    public void SemConfiguracao_ProcuraNoPath()
    {
        var path = string.Join(Path.PathSeparator, Path.Combine("X:", "outra"), Tools);

        Assert.Equal(Exe, ScrcpyLocator.Find(null, path, p => p == Exe));
    }

    [Fact]
    public void ConfiguracaoApontaParaAPasta_AchaOExecutavel()
    {
        Assert.Equal(Exe, ScrcpyLocator.Find(Tools, null, p => p == Exe));
    }

    [Fact]
    public void ConfiguracaoInexistente_NaoCaiParaOPath()
    {
        Assert.Null(ScrcpyLocator.Find(Path.Combine("X:", "nao-existe"), Tools, p => p == Exe));
    }

    [Fact]
    public void NadaEncontrado_RetornaNull()
    {
        Assert.Null(ScrcpyLocator.Find(null, Tools, _ => false));
    }
}
