namespace Toolbox.Contracts;

/// <summary>
/// 可选：视图的生命周期包装。插件返回它时，宿主适配层会挂载 <see cref="View"/>，
/// 并在卸载该插件<strong>之前</strong>调用 <see cref="Release"/> 做清理。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：<see cref="IToolboxToolView.CreateView"/> 返回的是中立类型 <see cref="object"/>，
/// 如果直接返回框架控件，宿主就没有任何"卸载前回调插件"的入口，
/// 插件里的 <c>DispatcherTimer</c>、事件订阅、Popup 就只能靠 GC 兜底（等于漏）。
/// </para>
/// <para>
/// <b>实现要求</b>：实现类本身<strong>不要派生任何 WPF 类型</strong>（不要 <c>: UserControl</c>、
/// 不要 <c>: Grid</c>、不要定义 <c>DependencyProperty</c>）。
/// 只要插件里存在"插件定义的 <c>DependencyObject</c> 派生类型"并被实例化，WPF 就会把该类型
/// 写进进程级缓存，ALC 将在多次强制 GC 后依然存活（本仓库冒烟测试实测）。
/// 视图对象请只作为<strong>构建器</strong>存在，挂到视觉树上的根节点用框架类型。
/// </para>
/// </remarks>
public interface IPluginViewLifetime
{
    /// <summary>要挂到视觉树上的根元素（WPF 插件请返回框架控件，如 <c>Grid</c>）。</summary>
    object View { get; }

    /// <summary>
    /// 卸载前的清理回调：退订事件、停掉定时器、关闭 Popup。
    /// 适配层会在把视图移出视觉树之后调用它，随后丢弃对 <see cref="View"/> 与本对象的引用。
    /// </summary>
    void Release();
}
