using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Wireflex.Tests.Fakes;

/// <summary>Guarda as mensagens de log já formatadas, para o teste conferir o que foi registrado.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];
    private readonly Channel<string> _arrived = Channel.CreateUnbounded<string>();

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    /// <summary>Espera chegar uma mensagem que atenda à condição (as que chegam antes e não atendem são descartadas).</summary>
    public async Task<string> WaitForAsync(Func<string, bool> match)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Real);
        while (true)
        {
            var message = await _arrived.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (match(message))
            {
                return message;
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_messages)
        {
            _messages.Add(message);
        }

        _arrived.Writer.TryWrite(message);
    }
}

/// <summary>Todas as categorias gravam no mesmo <see cref="Logger"/>.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    public RecordingLogger<object> Logger { get; } = new();

    public ILogger CreateLogger(string categoryName) => Logger;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
