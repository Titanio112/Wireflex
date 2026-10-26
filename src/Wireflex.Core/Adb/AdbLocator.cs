using Wireflex.Core.Processes;

namespace Wireflex.Core.Adb;

/// <summary>
/// Descobre qual adb usar: o caminho definido na configuração ou, se não houver, o primeiro no PATH.
/// </summary>
public static class AdbLocator
{
    public static string ExecutableName => ExecutableLocator.ExecutableName("adb");

    public static string? Find(string? configuredPath) =>
        Find(configuredPath, Environment.GetEnvironmentVariable("PATH"), File.Exists);

    public static string? Find(string? configuredPath, string? pathVariable, Func<string, bool> fileExists) =>
        ExecutableLocator.Find(ExecutableName, configuredPath, pathVariable, fileExists);
}
