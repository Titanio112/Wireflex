using Microsoft.Win32;
using Wireflex.Core.Hosting;

namespace Wireflex.Windows;

/// <summary>
/// Valor na chave Run do usuário atual (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>): o Windows abre o
/// comando ao entrar. Sem permissão de administrador e só para este usuário.
/// </summary>
public sealed class RunKeyStartupRegistration(
    string valueName = RunKeyStartupRegistration.DefaultValueName,
    string keyPath = RunKeyStartupRegistration.RunKeyPath) : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "Wireflex";

    public string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    public void Register(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
