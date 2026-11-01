using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Connection;

namespace Wireflex.Core.Hosting;

/// <summary>Onde o Windows guarda o que abre ao entrar (a chave Run do usuário atual). Os testes usam uma falsa.</summary>
public interface IStartupRegistration
{
    /// <summary>O comando registrado agora, ou <c>null</c> se não houver.</summary>
    string? GetCommand();

    void Register(string command);

    void Unregister();
}

/// <summary>
/// "Iniciar com o Windows": a config (<c>startWithWindows</c>) manda e o registro segue. Com a opção ligada, o
/// caminho é regravado a cada partida (a versão portátil pode ter mudado de pasta).
/// </summary>
public sealed class StartWithWindows
{
    private readonly IStartupRegistration _registration;
    private readonly PhoneCatalog _catalog;
    private readonly string _command;
    private readonly ILogger _logger;

    /// <param name="executablePath">O executável do Wireflex (ex: <c>Environment.ProcessPath</c>).</param>
    public StartWithWindows(IStartupRegistration registration, PhoneCatalog catalog, string executablePath, ILogger? logger = null)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _command = CommandFor(executablePath);
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsEnabled => _catalog.StartWithWindows;

    /// <summary>Comando gravado na chave Run: o caminho entre aspas (pode ter espaços).</summary>
    public static string CommandFor(string executablePath) => $"\"{executablePath}\"";

    /// <summary>Liga ou desliga: muda o registro e grava na config.</summary>
    public async Task SetAsync(bool enabled)
    {
        Apply(enabled);
        await _catalog.SetStartWithWindowsAsync(enabled).ConfigureAwait(false);
    }

    /// <summary>Na partida: deixa o registro como a config diz.</summary>
    public void Sync() => Apply(IsEnabled);

    private void Apply(bool enabled)
    {
        var current = _registration.GetCommand();
        if (enabled && current != _command)
        {
            _registration.Register(_command);
            _logger.LogInformation("Iniciar com o Windows: ligado{Updated}", current is null ? string.Empty : " (caminho atualizado)");
        }
        else if (!enabled && current is not null)
        {
            _registration.Unregister();
            _logger.LogInformation("Iniciar com o Windows: desligado");
        }
    }
}
