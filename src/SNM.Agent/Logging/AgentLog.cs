using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SNM.Agent.Logging;

/// <summary>Single-line UTC stdout logger: 2026-09-07T00:12:34.567Z INFO  message (docs/PROTOCOL.md 7.10). No reflection, no console provider package.</summary>
internal static class AgentLog
{
    private static readonly Lock Gate = new();
    public static LogLevel MinimumLevel = LogLevel.Information;

    public static bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;

    public static void Trace(string message) => Write(LogLevel.Trace, "snm", message, null);
    public static void Debug(string message) => Write(LogLevel.Debug, "snm", message, null);
    public static void Info(string message) => Write(LogLevel.Information, "snm", message, null);
    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warning, "snm", message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, "snm", message, ex);

    public static void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (!IsEnabled(level)) return;
        var sb = new StringBuilder(160);
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        sb.Append(' ').Append(LevelText(level)).Append(' ');
        if (category != "snm") sb.Append('[').Append(ShortCategory(category)).Append("] ");
        sb.Append(message.Replace('\n', ' '));
        if (exception is not null) sb.Append(" :: ").Append(exception.GetType().Name).Append(": ").Append(exception.Message.Replace('\n', ' '));
        var line = sb.ToString();
        lock (Gate)
        {
            Console.Out.WriteLine(line);
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "?????",
    };

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }
}

/// <summary>ILoggerProvider adapter so the SignalR client logs through <see cref="AgentLog"/>.</summary>
internal sealed class AgentLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AgentLogger(categoryName);
    public void Dispose() { }

    private sealed class AgentLogger(string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => AgentLog.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            AgentLog.Write(logLevel, category, formatter(state, exception), exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Suppresses repeated collector errors: at most one log line per key per 5 minutes.</summary>
internal sealed class ErrorThrottle
{
    private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);
    private static readonly long Window = TimeSpan.FromMinutes(5).Ticks;

    public void Warn(string key, string message, Exception? ex = null)
    {
        var now = DateTime.UtcNow.Ticks;
        lock (_last)
        {
            if (_last.TryGetValue(key, out var last) && now - last < Window) return;
            _last[key] = now;
        }
        AgentLog.Warn(message, ex);
    }
}
