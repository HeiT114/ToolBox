using Toolbox.Views.Wpf;

namespace Toolbox.App.ViewModels;

/// <summary>
/// 一个插件工具视图的标签页。
/// </summary>
/// <remarks>
/// ⚠ <see cref="ViewHost"/> 内部承载的是<strong>插件 ALC 里的对象</strong>。
/// 插件卸载前必须先 <see cref="Release"/>（把视图移出视觉树 + 回调插件清理），
/// 否则回收验证会失败——主程序通过 <c>PluginManager.PluginUnloading</c> 事件保证这一点。
/// </remarks>
public sealed class ToolTabViewModel(string header, PluginToolViewHost viewHost, string pluginId, string toolId)
{
    public string Header { get; } = header;

    public string PluginId { get; } = pluginId;

    public string ToolId { get; } = toolId;

    public PluginToolViewHost ViewHost { get; } = viewHost;

    public void Release() => ViewHost.Release();

    /// <summary>
    /// 数据绑定的 TabItem 会用这里的返回值作为 UI Automation 名称，
    /// 因此必须给出可读标题（否则无障碍/UIA 工具只能看到类名）。
    /// </summary>
    public override string ToString() => Header;
}
