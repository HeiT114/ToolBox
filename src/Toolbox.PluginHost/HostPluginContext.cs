using Microsoft.Extensions.Logging;
using Toolbox.Contracts;

namespace Toolbox.PluginHost;

/// <summary>
/// <see cref="IPluginContext"/> 的宿主实现：把"插件能看到的世界"收敛成一个对象。
/// </summary>
/// <remarks>
/// 注意 <see cref="Services"/> 是<strong>后绑定</strong>的：插件私有 ServiceProvider 需要先建好，
/// 再回填到这里（因为容器里注册了 <see cref="IPluginContext"/> 自身，存在构造环）。
/// </remarks>
internal sealed class HostPluginContext(
    string pluginId,
    string pluginDirectory,
    string dataDirectory,
    IPluginLogger logger,
    IPluginConfiguration configuration) : IPluginContext
{
    private IServiceProvider? _services;

    public string PluginId { get; } = pluginId;

    public string PluginDirectory { get; } = pluginDirectory;

    public string DataDirectory { get; } = dataDirectory;

    public IPluginLogger Logger { get; } = logger;

    public IPluginConfiguration Configuration { get; } = configuration;

    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException($"插件 {PluginId} 的 DI 容器尚未就绪。");

    internal void AttachServices(IServiceProvider services) => _services = services;

    /// <summary>宿主在卸载时断开引用，避免上下文把插件 DI 容器钉住。</summary>
    internal void Detach()
    {
        _services = null;
    }
}

/// <summary>把插件日志转发进主程序日志管线，并自动带上插件 Id 作用域。</summary>
internal sealed class PluginLoggerAdapter(ILogger logger, string pluginId) : IPluginLogger
{
    public bool IsEnabled(PluginLogLevel level) => logger.IsEnabled(Map(level));

    public void Log(PluginLogLevel level, string message, Exception? exception = null) =>
        logger.Log(Map(level), exception, "[{PluginId}] {Message}", pluginId, message);

    private static LogLevel Map(PluginLogLevel level) => level switch
    {
        PluginLogLevel.Trace => LogLevel.Trace,
        PluginLogLevel.Debug => LogLevel.Debug,
        PluginLogLevel.Information => LogLevel.Information,
        PluginLogLevel.Warning => LogLevel.Warning,
        PluginLogLevel.Error => LogLevel.Error,
        PluginLogLevel.Critical => LogLevel.Critical,
        _ => LogLevel.Information,
    };
}

/// <summary>只读扁平配置视图：插件清单 settings 与主程序配置合并后的结果。</summary>
internal sealed class PluginConfigurationAdapter(IReadOnlyDictionary<string, string?> values) : IPluginConfiguration
{
    public string? Get(string key) => values.TryGetValue(key, out var value) ? value : null;

    public bool TryGet(string key, out string? value) => values.TryGetValue(key, out value);

    public IReadOnlyDictionary<string, string?> GetAll() => values;
}
