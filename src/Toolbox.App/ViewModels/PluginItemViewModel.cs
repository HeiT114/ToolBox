using Toolbox.PluginHost;

namespace Toolbox.App.ViewModels;

/// <summary>
/// 插件在 UI 里的投影。
/// </summary>
/// <remarks>
/// ⚠ 这里只保留<strong>字符串与枚举</strong>（来自 <see cref="PluginDescriptor"/>），
/// 绝不缓存插件实例、工具实例或 <c>Type</c>/<c>Assembly</c>。
/// UI 是生命周期最长的对象之一，一旦它引用了插件对象，卸载就永远收不回 ALC。
/// </remarks>
public sealed class PluginItemViewModel(PluginDescriptor descriptor)
{
    public PluginDescriptor Descriptor { get; } = descriptor;

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    public string Version => Descriptor.Version ?? "-";

    public PluginState State => Descriptor.State;

    public PluginSourceKind SourceKind => Descriptor.SourceKind;

    public string ToolsText => Descriptor.ToolNames.Count == 0 ? "-" : string.Join(", ", Descriptor.ToolNames);

    public string AssemblyCountText => Descriptor.LoadedAssemblyNames.Count == 0
        ? "-"
        : Descriptor.LoadedAssemblyNames.Count.ToString();

    public string Error => Descriptor.Error ?? string.Empty;

    public IReadOnlyList<string> ToolNames => Descriptor.ToolNames;

    /// <summary>状态的中文短标签（列表里的状态药丸）。</summary>
    public string StateText => State switch
    {
        PluginState.Discovered => "未加载",
        PluginState.Loading => "加载中",
        PluginState.Loaded => "已加载",
        PluginState.Failed => "失败",
        PluginState.Unloading => "卸载中",
        PluginState.Unloaded => "已卸载",
        _ => State.ToString(),
    };

    /// <summary>
    /// 状态的语义色调（供 UI 的 DataTrigger 用）。用字符串而不是枚举，
    /// 避免 DataTrigger 里做枚举转换。
    /// </summary>
    public string StateTone => State switch
    {
        PluginState.Loaded => "ok",
        PluginState.Failed => "error",
        PluginState.Loading or PluginState.Unloading => "busy",
        _ => "idle",
    };

    public override string ToString() => $"{Id} ({State})";
}
