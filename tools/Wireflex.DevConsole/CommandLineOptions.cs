using Wireflex.Core.Configuration;

namespace Wireflex.DevConsole;

internal sealed record CommandLineOptions(
    string? AdbPath,
    string? ScrcpyPath,
    ConnectionMode? ConnectionMode,
    bool Verbose,
    bool Real)
{
    public const string Usage = """
        Wireflex DevConsole: liga o monitor real do adb, o motor de decisão e o coordenador de conexão.

        Padrão: SIMULADO. O monitor só lê; as ações aparecem na tela e nada é executado (o scrcpy não abre,
        nenhum comando de conexão vai para o celular e o config.json não é gravado).
        Com --real: abre o scrcpy, liga o modo TCP/IP e conecta pelo Wi-Fi, e grava os celulares aprendidos
        em %APPDATA%\Wireflex\config.json.

        Uso: Wireflex.DevConsole [opções]
          --real                executa as ações de verdade (sem isto, tudo é simulado)
          --adb <caminho>       adb.exe a usar (ou a pasta dele). Sem isso: config, depois PATH.
          --scrcpy <caminho>    scrcpy.exe a usar (ou a pasta dele). Sem isso: config, depois PATH.
          --modo <modo>         modo de conexão global, só em memória: auto | perguntar | nunca
          --verbose             mostra também os logs informativos do núcleo
          -h, --help            mostra esta ajuda
        """;

    /// <returns><c>false</c> quando é para sair: ajuda pedida (<paramref name="error"/> nulo) ou argumento inválido.</returns>
    public static bool TryParse(string[] args, out CommandLineOptions options, out string? error)
    {
        options = new CommandLineOptions(null, null, null, false, false);
        error = null;

        string? adbPath = null;
        string? scrcpyPath = null;
        ConnectionMode? mode = null;
        var verbose = false;
        var real = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "/?":
                    return false;
                case "--verbose":
                    verbose = true;
                    break;
                case "--real":
                    real = true;
                    break;
                case "--adb":
                    if (!TryReadPath(args, ref i, out adbPath))
                    {
                        error = "Faltou o caminho depois de --adb.";
                        return false;
                    }

                    break;
                case "--scrcpy":
                    if (!TryReadPath(args, ref i, out scrcpyPath))
                    {
                        error = "Faltou o caminho depois de --scrcpy.";
                        return false;
                    }

                    break;
                case "--modo":
                    if (++i >= args.Length || !TryParseMode(args[i], out var parsed))
                    {
                        error = "Use --modo auto, --modo perguntar ou --modo nunca.";
                        return false;
                    }

                    mode = parsed;
                    break;
                default:
                    error = $"Argumento desconhecido: {args[i]}";
                    return false;
            }
        }

        options = new CommandLineOptions(adbPath, scrcpyPath, mode, verbose, real);
        return true;
    }

    public static bool TryParseMode(string text, out ConnectionMode mode)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "auto" or "automatico" or "automático":
                mode = Core.Configuration.ConnectionMode.Automatic;
                return true;
            case "perguntar":
                mode = Core.Configuration.ConnectionMode.Ask;
                return true;
            case "nunca":
                mode = Core.Configuration.ConnectionMode.Never;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static bool TryReadPath(string[] args, ref int i, out string? path)
    {
        path = ++i < args.Length && !string.IsNullOrWhiteSpace(args[i]) ? args[i] : null;
        return path is not null;
    }
}
