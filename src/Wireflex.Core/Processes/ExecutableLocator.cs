namespace Wireflex.Core.Processes;

/// <summary>
/// Procura um executável: no caminho configurado (arquivo ou pasta) ou, se não houver, no PATH.
/// </summary>
public static class ExecutableLocator
{
    /// <param name="executableName">Nome do arquivo (ex: <c>adb.exe</c>).</param>
    /// <param name="configuredPath">Caminho configurado. Se preenchido e não existir, retorna <c>null</c> (não cai para o PATH).</param>
    public static string? Find(string executableName, string? configuredPath, string? pathVariable, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (fileExists(expanded))
            {
                return expanded;
            }

            // A configuração pode apontar para a pasta em vez do executável.
            var inFolder = Path.Combine(expanded, executableName);
            return fileExists(inFolder) ? inFolder : null;
        }

        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (var folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim().Trim('"'), executableName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Nome do executável na plataforma atual (<c>.exe</c> no Windows).</summary>
    public static string ExecutableName(string baseName) => OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
}
