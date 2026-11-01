using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Hosting;

namespace Wireflex.Tests.Hosting;

/// <summary>Registro falso: nada é gravado no Windows.</summary>
public sealed class StartWithWindowsTests
{
    private const string Exe = @"C:\Programas\Wireflex Portatil\Wireflex.exe";
    private const string Command = "\"" + Exe + "\"";

    private readonly FakeRegistration _registration = new();
    private readonly CountingStore _store = new();

    [Fact]
    public async Task Ligar_RegistraEGravaNaConfig()
    {
        var startup = Create(new WireflexConfig());

        await startup.SetAsync(true);

        Assert.Equal(Command, _registration.Command);
        Assert.True(startup.IsEnabled);
        Assert.True(_store.Last!.Global.StartWithWindows);
    }

    [Fact]
    public async Task Desligar_TiraDoRegistroEGravaNaConfig()
    {
        _registration.Command = Command;
        var startup = Create(new WireflexConfig { Global = new GlobalSettings { StartWithWindows = true } });

        await startup.SetAsync(false);

        Assert.Null(_registration.Command);
        Assert.False(_store.Last!.Global.StartWithWindows);
    }

    [Fact]
    public void Partida_ComAOpcaoLigada_AtualizaOCaminho()
    {
        // A versão portátil mudou de pasta.
        _registration.Command = "\"C:\\Antigo\\Wireflex.exe\"";
        var startup = Create(new WireflexConfig { Global = new GlobalSettings { StartWithWindows = true } });

        startup.Sync();

        Assert.Equal(Command, _registration.Command);
    }

    [Fact]
    public void Partida_ComAOpcaoDesligada_TiraDoRegistro()
    {
        _registration.Command = Command;

        Create(new WireflexConfig()).Sync();

        Assert.Null(_registration.Command);
    }

    [Fact]
    public void Partida_JaCerto_NaoMexe()
    {
        _registration.Command = Command;

        Create(new WireflexConfig { Global = new GlobalSettings { StartWithWindows = true } }).Sync();

        Assert.Equal(0, _registration.Writes);
    }

    private StartWithWindows Create(WireflexConfig config) =>
        new(_registration, new PhoneCatalog(config, _store), Exe);

    private sealed class FakeRegistration : IStartupRegistration
    {
        public string? Command { get; set; }

        public int Writes { get; private set; }

        public string? GetCommand() => Command;

        public void Register(string command)
        {
            Command = command;
            Writes++;
        }

        public void Unregister()
        {
            Command = null;
            Writes++;
        }
    }

    private sealed class CountingStore : IConfigStore
    {
        public WireflexConfig? Last { get; private set; }

        public Task<WireflexConfig> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WireflexConfig());

        public Task SaveAsync(WireflexConfig config, CancellationToken cancellationToken = default)
        {
            Last = config;
            return Task.CompletedTask;
        }
    }
}
