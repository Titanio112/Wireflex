using Microsoft.Win32;
using Wireflex.Windows;

namespace Wireflex.Tests.Windows;

/// <summary>Registro real, numa subchave só de teste em HKCU (nunca na chave Run de verdade), apagada no fim.</summary>
public sealed class RunKeyStartupRegistrationTests : IDisposable
{
    private readonly string _keyPath = @"Software\Wireflex.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RunKeyStartupRegistration _registration;

    public RunKeyStartupRegistrationTests()
    {
        _registration = new RunKeyStartupRegistration("Wireflex", _keyPath);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\Wireflex.Tests", throwOnMissingSubKey: false);

    [Fact]
    public void RegistraLeETira()
    {
        Assert.Null(_registration.GetCommand());

        _registration.Register("\"C:\\Programas\\Wireflex.exe\"");
        Assert.Equal("\"C:\\Programas\\Wireflex.exe\"", _registration.GetCommand());

        _registration.Unregister();
        Assert.Null(_registration.GetCommand());
    }

    [Fact]
    public void TirarSemTerRegistrado_NaoFalha()
    {
        _registration.Unregister();

        Assert.Null(_registration.GetCommand());
    }

    [Fact]
    public void ChaveRunPadrao_EADoUsuarioAtual()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RunKeyStartupRegistration.RunKeyPath);
    }
}
