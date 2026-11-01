using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Hosting;
using Wireflex.Tests.Fakes;

namespace Wireflex.Tests.Hosting;

public sealed class ToolPathsTests
{
    private const string Option = @"C:\Ferramentas\opcao\adb.exe";
    private const string Configured = @"C:\Ferramentas\config\adb.exe";
    private const string FromPath = @"C:\Ferramentas\path\adb.exe";

    [Fact]
    public void Opcao_TemPrioridade()
    {
        var found = ToolPaths.Resolve(Option, Configured, Find(Option, Configured));

        Assert.Equal(new ToolPath(Option, ToolSource.Option), found);
    }

    [Fact]
    public void OpcaoQueNaoExiste_NaoCaiParaOutro()
    {
        Assert.Null(ToolPaths.Resolve(Option, Configured, Find(Configured)));
    }

    [Fact]
    public void SemOpcao_UsaAConfig()
    {
        Assert.Equal(new ToolPath(Configured, ToolSource.Config), ToolPaths.Resolve(null, Configured, Find(Configured)));
    }

    [Fact]
    public void ConfigQueNaoExiste_ProcuraNoPath()
    {
        Assert.Equal(new ToolPath(FromPath, ToolSource.Path), ToolPaths.Resolve(null, Configured, Find()));
    }

    [Fact]
    public void ModoReal_PrecisaDoScrcpy()
    {
        var settings = new WireflexRuntimeSettings
        {
            AdbPath = FromPath,
            Real = true,
            Catalog = new PhoneCatalog(new WireflexConfig(), null),
            Notifier = new RecordingNotifier(),
        };

        Assert.Throws<ArgumentException>(() => new WireflexRuntime(settings));
    }

    /// <summary>Só os caminhos dados "existem"; <c>null</c> (procurar no PATH) acha <see cref="FromPath"/>.</summary>
    private static Func<string?, string?> Find(params string[] existing) =>
        path => path is null ? FromPath : existing.Contains(path) ? path : null;
}
