using Wireflex.Core.Logging;

namespace Wireflex.DevConsole;

/// <summary>
/// Escreve na tela com horário, uma linha por vez (seguro entre threads). Todo texto passa pelo
/// mascaramento de IP como rede de segurança; serial já chega mascarado por <see cref="Format"/>.
/// </summary>
internal sealed class ConsoleOutput
{
    private readonly object _sync = new();

    public void Info(string message) => Write("info ", message, null);

    public void Event(string message) => Write("evento", message, ConsoleColor.Cyan);

    public void Action(string message) => Write("ação ", message, ConsoleColor.Green);

    public void Idle(string message) => Write("ação ", message, ConsoleColor.DarkGray);

    public void Warning(string message) => Write("aviso", message, ConsoleColor.Yellow);

    public void Error(string message) => Write("erro ", message, ConsoleColor.Red);

    public void Log(string message) => Write("log  ", message, ConsoleColor.DarkYellow);

    /// <summary>Texto sem horário (ajuda, listas).</summary>
    public void Plain(string text)
    {
        lock (_sync)
        {
            Console.WriteLine(Sensitive.Scrub(text));
        }
    }

    private void Write(string tag, string message, ConsoleColor? color)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {Sensitive.Scrub(message)}";
        lock (_sync)
        {
            if (color is { } c)
            {
                Console.ForegroundColor = c;
            }

            Console.WriteLine(line);

            if (color is not null)
            {
                Console.ResetColor();
            }
        }
    }
}
