using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Wireflex.Core.Logging;

/// <summary>
/// Grava logs em arquivos diários (<c>wireflex-AAAAMMDD.log</c>) e apaga os antigos.
/// Todo texto passa por <see cref="Sensitive.ScrubIpAddresses"/> antes de ir para o disco.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const string FilePrefix = "wireflex-";
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly TimeProvider _time;
    private bool _disposed;

    public FileLoggerProvider(
        string directory,
        LogLevel minimumLevel = LogLevel.Information,
        int retainDays = 7,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _minimumLevel = minimumLevel;
        _time = timeProvider ?? TimeProvider.System;

        Directory.CreateDirectory(_directory);
        DeleteOldFiles(retainDays);
    }

    public string CurrentFilePath =>
        Path.Combine(_directory, $"{FilePrefix}{_time.GetLocalNow():yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder()
            .Append(_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(LevelName(level)).Append("] ")
            .Append(ShortCategory(category)).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        var line = Sensitive.ScrubIpAddresses(builder.ToString()) + Environment.NewLine;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                File.AppendAllText(CurrentFilePath, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Log nunca pode derrubar o app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void DeleteOldFiles(int retainDays)
    {
        var limit = _time.GetLocalNow().Date.AddDays(-retainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, $"{FilePrefix}*.log"))
        {
            var datePart = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date < limit)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
