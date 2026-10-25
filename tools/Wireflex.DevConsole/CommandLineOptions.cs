using Wireflex.Core.Configuration;

namespace Wireflex.DevConsole;

internal sealed record CommandLineOptions(string? AdbPath, ConnectionMode? ConnectionMode, bool Verbose)
{
    public const string Usage = """
        Wireflex DevConsole: liga o monitor real do adb e o motor de decisão e mostra eventos e ações.
        Nada é executado: o scrcpy não abre e nenhum comando de conexão é enviado ao celular.

        Uso: Wireflex.DevConsole [opções]
          --adb <caminho>       adb.exe a usar (ou a pasta dele). Sem isso: config, depois PATH.
          --modo <modo>         modo de conexão global, só em memória: auto | perguntar | nunca
          --verbose             mostra também os logs informativos do núcleo
          -h, --help            mostra esta ajuda
        """;

    /// <returns><c>false</c> quando é para sair: ajuda pedida (<paramref name="error"/> nulo) ou argumento inválido.</returns>
    public static bool TryParse(string[] args, out CommandLineOptions options, out string? error)
    {
        options = new CommandLineOptions(null, null, false);
        error = null;

        string? adbPath = null;
        ConnectionMode? mode = null;
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "/?":
                    return false;
                case "--verbose":
                    verbose = true;
                    break;
                case "--adb":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        error = "Faltou o caminho depois de --adb.";
                        return false;
                    }

                    adbPath = args[i];
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

        options = new CommandLineOptions(adbPath, mode, verbose);
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
}
