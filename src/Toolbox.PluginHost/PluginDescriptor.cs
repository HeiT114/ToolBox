namespace Toolbox.PluginHost;

/// <summary>插件在宿主里的状态机。UI 直接绑定它。</summary>
public enum PluginState
{
    /// <summary>发现了来源但还没加载。</summary>
    Discovered = 0,

    Loading = 1,

    Loaded = 2,

    /// <summary>加载或初始化失败（<see cref="PluginDescriptor.Error"/> 里有原因）。</summary>
    Failed = 3,

    Unloading = 4,

    /// <summary>已请求卸载；ALC 是否真的被回收看 <see cref="PluginUnloadReport.ContextCollected"/>。</summary>
    Unloaded = 5,
}

/// <summary>
/// 面向 UI 的插件描述：<strong>只含宿主类型和字符串</strong>。
/// </summary>
/// <remarks>
/// 这是卸载设计的一部分：宿主可以长期持有 <see cref="PluginDescriptor"/>，
/// 但它绝不能持有插件里的 <c>Type</c> / 实例 / <c>Assembly</c>，
/// 否则描述对象本身就会钉住 ALC。程序集相关的东西一律放在 <see cref="PluginHandle"/> 里，
/// 卸载时随句柄一起丢弃。
/// </remarks>
public sealed class PluginDescriptor
{
    public string Id { get; internal set; } = string.Empty;

    public string DisplayName { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public string? Version { get; internal set; }

    public string? Author { get; internal set; }

    /// <summary>插件来源（目录或 zip 的原始路径），UI"打开所在位置"用。</summary>
    public string SourcePath { get; internal set; } = string.Empty;

    public PluginSourceKind SourceKind { get; internal set; }

    /// <summary>实际被加载的内容根：目录插件=原目录；zip 插件=影子解包目录。</summary>
    public string ContentRoot { get; internal set; } = string.Empty;

    public string? EntryAssemblyPath { get; internal set; }

    public PluginState State { get; internal set; } = PluginState.Discovered;

    public string? Error { get; internal set; }

    /// <summary>插件提供的工具名（加载后才填充；元素是字符串，不含插件类型）。</summary>
    public IReadOnlyList<string> ToolNames { get; internal set; } = [];

    /// <summary>该插件 ALC 里的程序集简单名（诊断/排查"是不是插件自己带了一份 Contracts"）。</summary>
    public IReadOnlyList<string> LoadedAssemblyNames { get; internal set; } = [];

    public DateTimeOffset? LoadedAt { get; internal set; }

    public bool IsLoaded => State == PluginState.Loaded;

    public override string ToString() => $"{Id} {Version ?? "?"} [{State}]";
}
