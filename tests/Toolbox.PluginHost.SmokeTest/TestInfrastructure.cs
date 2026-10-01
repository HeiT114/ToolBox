using Microsoft.Extensions.Logging;
using Toolbox.PluginHost;

namespace Toolbox.PluginHost.SmokeTest;

/// <summary>极简断言器：输出 [PASS]/[FAIL] 行，最后汇总，失败时进程退出码为 1。</summary>
internal static class Check
{
    public static int Total { get; private set; }

    public static int Failures { get; private set; }

    public static void That(bool condition, string description)
    {
        Total++;
        if (condition)
        {
            Console.WriteLine($"  [PASS] {description}");
            return;
        }

        Failures++;
        Console.WriteLine($"  [FAIL] {description}");
    }

    public static void Equal<T>(T expected, T actual, string description)
    {
        var ok = EqualityComparer<T>.Default.Equals(expected, actual);
        That(ok, ok ? description : $"{description}（期望 {expected}，实际 {actual}）");
    }

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }
}

/// <summary>把日志直接打到控制台，便于把"插件加载/卸载"的过程和断言结果对照着看。</summary>
internal sealed class ConsoleLoggerProvider(bool verbose) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, verbose);

    public void Dispose()
    {
    }

    private sealed class ConsoleLogger(string category, bool verbose) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            verbose ? logLevel >= LogLevel.Debug : logLevel >= LogLevel.Information;

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

            var shortCategory = category.Split('.')[^1];
            Console.WriteLine($"    [log:{logLevel,-11}] {shortCategory}: {formatter(state, exception)}");
            if (exception is not null)
            {
                Console.WriteLine($"        {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}

/// <summary>
/// 模拟"主程序配置"：按插件 Id 提供覆盖配置，用来验证 plugin.json 默认值 &lt; 主程序配置。
/// </summary>
internal sealed class TestSettingsProvider : IPluginSettingsProvider
{
    private readonly Dictionary<string, Dictionary<string, string?>> _settings = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string pluginId, string key, string? value)
    {
        if (!_settings.TryGetValue(pluginId, out var bucket))
        {
            bucket = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            _settings[pluginId] = bucket;
        }

        bucket[key] = value;
    }

    public IReadOnlyDictionary<string, string?> GetSettings(string pluginId) =>
        _settings.TryGetValue(pluginId, out var bucket) ? bucket : new Dictionary<string, string?>();
}
