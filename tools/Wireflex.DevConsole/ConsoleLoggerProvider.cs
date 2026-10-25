using Microsoft.Extensions.Logging;

namespace Wireflex.DevConsole;

/// <summary>Mostra os logs do núcleo na tela, a partir do nível escolhido.</summary>
internal sealed class ConsoleLoggerProvider(ConsoleOutput output, LogLevel minimumLevel) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(output, minimumLevel, ShortName(categoryName));

    public void Dispose()
    {
    }

    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class ConsoleLogger(ConsoleOutput output, LogLevel minimumLevel, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $" ({exception.GetType().Name}: {exception.Message})";
            }

            output.Log($"{logLevel} {category}: {message}");
        }
    }
}
