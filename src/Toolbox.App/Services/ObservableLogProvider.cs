using Microsoft.Extensions.Logging;

namespace Toolbox.App.Services;

/// <summary>UI 日志列表里的一行。</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff} [{Level,-11}] {Trim(Category),-22} {Message}";

    private static string Trim(string category)
    {
        var index = category.LastIndexOf('.');
        return index >= 0 && index < category.Length - 1 ? category[(index + 1)..] : category;
    }
}

/// <summary>
/// 供 UI 绑定的日志缓冲。**必须**只在 UI 线程上修改（由 <see cref="ObservableLogProvider"/> 负责 marshal）。
/// </summary>
public sealed class ObservableLogSink
{
    private const int MaxEntries = 500;

    public System.Collections.ObjectModel.ObservableCollection<LogEntry> Entries { get; } = [];

    public void Add(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
    }
}

/// <summary>把日志写进 <see cref="ObservableLogSink"/>，并从任意线程 marshal 回 UI 线程。</summary>
internal sealed class ObservableLogProvider(ObservableLogSink sink, System.Windows.Threading.Dispatcher dispatcher) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ObservableLogger(sink, dispatcher, categoryName);

    public void Dispose()
    {
    }

    private sealed class ObservableLogger(
        ObservableLogSink sink,
        System.Windows.Threading.Dispatcher dispatcher,
        string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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

            var message = exception is null
                ? formatter(state, exception)
                : $"{formatter(state, exception)} | {exception.GetType().Name}: {exception.Message}";

            var entry = new LogEntry(DateTimeOffset.Now, logLevel, category, message);

            // 插件加载/卸载发生在线程池线程上，而 ObservableCollection 只能在 UI 线程改。
            if (dispatcher.CheckAccess())
            {
                sink.Add(entry);
            }
            else
            {
                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => sink.Add(entry)));
            }
        }
    }
}
