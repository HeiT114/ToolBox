namespace Toolbox.Contracts;

/// <summary>日志级别。刻意与 Microsoft.Extensions.Logging 解耦，避免契约层被具体日志实现绑架。</summary>
public enum PluginLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>插件可用的日志抽象。宿主实现会把它转发到主程序日志管线（并带上插件 Id 作用域）。</summary>
public interface IPluginLogger
{
    /// <summary>先判断再拼字符串，避免卸载路径上的无谓分配。</summary>
    bool IsEnabled(PluginLogLevel level);

    void Log(PluginLogLevel level, string message, Exception? exception = null);
}

/// <summary>便捷扩展：让插件侧写日志的体验接近 <c>ILogger</c>。</summary>
public static class PluginLoggerExtensions
{
    public static void LogTrace(this IPluginLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(PluginLogLevel.Trace))
        {
            logger.Log(PluginLogLevel.Trace, message);
        }
    }

    public static void LogDebug(this IPluginLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(PluginLogLevel.Debug))
        {
            logger.Log(PluginLogLevel.Debug, message);
        }
    }

    public static void LogInformation(this IPluginLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(PluginLogLevel.Information))
        {
            logger.Log(PluginLogLevel.Information, message);
        }
    }

    public static void LogWarning(this IPluginLogger logger, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(PluginLogLevel.Warning))
        {
            logger.Log(PluginLogLevel.Warning, message, exception);
        }
    }

    public static void LogError(this IPluginLogger logger, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(PluginLogLevel.Error))
        {
            logger.Log(PluginLogLevel.Error, message, exception);
        }
    }
}
