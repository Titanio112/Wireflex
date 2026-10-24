namespace Wireflex.Core;

/// <summary>
/// Pastas do usuário usadas pelo Wireflex. Nada disso fica no repositório.
/// </summary>
public static class WireflexPaths
{
    /// <summary><c>%APPDATA%\Wireflex</c>: configuração.</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);

    /// <summary><c>%LOCALAPPDATA%\Wireflex</c>: dados locais da máquina.</summary>
    public static string LocalDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);

    /// <summary><c>%LOCALAPPDATA%\Wireflex\logs</c>.</summary>
    public static string LogsDirectory => Path.Combine(LocalDataDirectory, "logs");

    /// <summary><c>%LOCALAPPDATA%\Wireflex\tools</c>: scrcpy e adb baixados (Etapa 2).</summary>
    public static string ToolsDirectory => Path.Combine(LocalDataDirectory, "tools");
}
