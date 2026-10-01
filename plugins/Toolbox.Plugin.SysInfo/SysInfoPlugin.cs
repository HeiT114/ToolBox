using Toolbox.Contracts;
using Toolbox.Plugin.SysInfo.Core;

namespace Toolbox.Plugin.SysInfo;

/// <summary>
/// 初始测试插件：检测当前系统环境。
/// </summary>
/// <remarks>
/// <para>
/// 一个插件同时扮演三个角色，刚好覆盖宿主要验证的所有路径：
/// </para>
/// <list type="number">
///   <item><see cref="IToolboxPlugin"/>：生命周期（初始化/退出都在日志里留痕）；</item>
///   <item><see cref="IToolboxToolProvider"/>：声明工具清单（展示名/分类/排序/是否有界面）；</item>
///   <item>工具 <c>sysinfo</c> 同时实现 <see cref="IToolboxTool"/>（脚本可调用）与 <see cref="IToolboxToolView"/>（带界面）。</item>
/// </list>
/// <para>
/// 真实探测逻辑放在<strong>私有依赖</strong> <c>Toolbox.Plugin.SysInfo.Core</c> 里，
/// 宿主与契约层完全不知道它的存在 —— 这样同时验证了 AssemblyDependencyResolver 的隔离能力。
/// </para>
/// </remarks>
[ToolboxPlugin("sysinfo", DisplayName = "系统环境检测", Version = "1.0.0",
    Description = "检测当前系统环境：机器/用户/操作系统/运行时/CPU/内存/时区，并给出可读报告")]
public sealed class SysInfoPlugin : IToolboxPlugin, IToolboxToolProvider
{
    private IPluginContext? _context;

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;

        var snapshot = SystemEnvironmentProbe.Capture();
        context.Logger.LogInformation(
            $"系统环境检测插件已初始化：{snapshot.Summarize()}；数据目录 = {context.DataDirectory}");

        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        // 卸载纪律：自己也要把引用清干净（宿主侧还有一个"视图必须先摘掉"的约定）。
        _context?.Logger.LogInformation("系统环境检测插件正在退出。");
        _context = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 「插件提供哪些工具」的声明入口：宿主拿它画列表，不需要实例化任何视图。
    /// 返回值全是契约/BCL 类型，可以安全跨 ALC 传递与缓存。
    /// </summary>
    public IReadOnlyList<ToolboxToolInfo> ProvideTools() =>
    [
        new ToolboxToolInfo(
            Id: "sysinfo",
            DisplayName: "环境报告",
            Description: "采集当前系统环境；支持 title（报告标题）与 filter（关键字过滤）",
            Category: "诊断",
            Order: 10,
            HasView: true),
    ];
}
