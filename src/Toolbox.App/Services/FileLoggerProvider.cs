using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Toolbox.App.Services;

/// <summary>
/// 极简文件日志：按天一个文件，写入串行化。
/// </summary>
/// <remarks>
/// 真实项目里建议换成成熟的滚动文件实现（Serilog / NLog / Microsoft.Extensions.Logging 的文件 Provider）。
/// 这里刻意手写，是为了让"日志是主程序职责"这件事在骨架里可见，且不引入额外依赖。
/// </remarks>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly Lock _sync = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        try
        {
            lock (_sync)
            {
                var path = Path.Combine(_directory, $"toolbox-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写失败绝不能影响主流程
            System.Diagnostics.Debug.WriteLine($"写日志失败：{ex.Message}");
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

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
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel,-11}] {category} {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line);
        }
    }
}
