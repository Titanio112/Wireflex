using Wireflex.Core.Processes;

namespace Wireflex.Core.Scrcpy;

/// <summary>
/// Descobre qual scrcpy usar, como o adb: o caminho configurado (arquivo ou pasta) ou o primeiro no PATH.
/// </summary>
public static class ScrcpyLocator
{
    public static string ExecutableName => ExecutableLocator.ExecutableName("scrcpy");

    public static string? Find(string? configuredPath) =>
        Find(configuredPath, Environment.GetEnvironmentVariable("PATH"), File.Exists);

    public static string? Find(string? configuredPath, string? pathVariable, Func<string, bool> fileExists) =>
        ExecutableLocator.Find(ExecutableName, configuredPath, pathVariable, fileExists);
}
