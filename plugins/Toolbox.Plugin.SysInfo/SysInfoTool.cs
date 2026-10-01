using Toolbox.Contracts;
using Toolbox.Plugin.SysInfo.Core;

namespace Toolbox.Plugin.SysInfo;

/// <summary>
/// 系统环境检测工具：既可脚本调用（<see cref="IToolboxTool"/>），也带界面（<see cref="IToolboxToolView"/>）。
/// </summary>
/// <remarks>
/// 参数：<c>title=报告标题</c>（缺省取插件配置）、<c>filter=关键字</c>（按 Key/标签/值过滤）。
/// 构造注入 <see cref="IPluginConfiguration"/> 是宿主提供的只读配置，
/// 优先级：<c>plugin.json.settings</c>（默认值）&lt; 主程序配置（覆盖）。
/// </remarks>
public sealed class SysInfoTool(IPluginLogger logger, IPluginConfiguration configuration)
    : IToolboxTool, IToolboxToolView
{
    public string Id => "sysinfo";

    public string DisplayName => "环境报告";

    public string? Category => "诊断";

    public int Order => 10;

    public string Name => Id;

    public string? Description => "检测当前系统环境（脚本调用：title=标题，filter=关键字）";

    /// <summary>
    /// 视图用纯 C# 代码构建（不使用 BAML）；返回 <see cref="IPluginViewLifetime"/> 包装，
    /// 让宿主适配层既能挂载（<c>View</c> 是框架控件 <c>Grid</c>），也能在卸载前回调插件清理。
    /// </summary>
    public object CreateView(IPluginContext context) =>
        new SysInfoView(context.Configuration, logger);

    public ValueTask<ToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken = default)
    {
        arguments.TryGetValue("title", out var title);
        arguments.TryGetValue("filter", out var filter);

        var snapshot = SystemEnvironmentProbe.Capture();
        var report = snapshot.ToReport(title ?? configuration.Get("title"), filter);

        logger.LogInformation(
            $"sysinfo 采集完成：{snapshot.Facts.Count} 项，filter={filter ?? "<无>"}");

        return ValueTask.FromResult(ToolResult.Ok(report));
    }
}
