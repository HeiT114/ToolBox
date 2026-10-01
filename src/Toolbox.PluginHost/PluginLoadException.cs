namespace Toolbox.PluginHost;

/// <summary>
/// 插件加载失败（定位入口、缺依赖、清单非法、找不到插件入口类型……）。
/// 与插件内部抛出的业务异常区分开，便于 UI 给出可操作提示。
/// </summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message)
        : base(message)
    {
    }

    public PluginLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>出问题的插件来源（可能为空：失败发生在来源确定之前）。</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// 失败时已经建立起来的描述对象（含 Id / 状态 / 错误原因），便于 UI 直接展示而不是重新猜一遍。
    /// 该对象只含字符串，不持有插件类型。
    /// </summary>
    public PluginDescriptor? Descriptor { get; init; }
}
