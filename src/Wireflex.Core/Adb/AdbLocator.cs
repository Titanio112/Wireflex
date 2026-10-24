namespace Wireflex.Core.Adb;

/// <summary>
/// Descobre qual adb usar: o caminho definido na configuração ou, se não houver, o primeiro no PATH.
/// </summary>
public static class AdbLocator
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "adb.exe" : "adb";

    public static string? Find(string? configuredPath) =>
        Find(configuredPath, Environment.GetEnvironmentVariable("PATH"), File.Exists);

    public static string? Find(string? configuredPath, string? pathVariable, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (fileExists(expanded))
            {
                return expanded;
            }

            // A configuração pode apontar para a pasta em vez do executável.
            var inFolder = Path.Combine(expanded, ExecutableName);
            return fileExists(inFolder) ? inFolder : null;
        }

        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (var folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim().Trim('"'), ExecutableName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
