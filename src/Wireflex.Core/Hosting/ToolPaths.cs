namespace Wireflex.Core.Hosting;

/// <summary>Um executável externo encontrado (adb ou scrcpy) e de onde veio o caminho.</summary>
public sealed record ToolPath(string Path, ToolSource Source);

public enum ToolSource
{
    /// <summary>Opção da linha de comando.</summary>
    Option,

    /// <summary><c>adbPath</c>/<c>scrcpyPath</c> na config.</summary>
    Config,

    /// <summary>PATH do sistema.</summary>
    Path,
}

public static class ToolPaths
{
    /// <summary>
    /// Ordem: opção da linha de comando (sem cair para outro se não existir), depois a config, depois o PATH.
    /// </summary>
    /// <param name="find">Confere um caminho; com <c>null</c>, procura no PATH (ex: <c>AdbLocator.Find</c>).</param>
    public static ToolPath? Resolve(string? fromOption, string? fromConfig, Func<string?, string?> find)
    {
        ArgumentNullException.ThrowIfNull(find);
        if (fromOption is not null)
        {
            return find(fromOption) is { } option ? new ToolPath(option, ToolSource.Option) : null;
        }

        if (!string.IsNullOrWhiteSpace(fromConfig) && find(fromConfig) is { } configured)
        {
            return new ToolPath(configured, ToolSource.Config);
        }

        return find(null) is { } found ? new ToolPath(found, ToolSource.Path) : null;
    }
}
