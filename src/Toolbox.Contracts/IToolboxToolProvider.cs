namespace Toolbox.Contracts;

/// <summary>
/// 插件可选的"工具清单声明入口"。
/// </summary>
/// <remarks>
/// <para>
/// 宿主本来就会反射发现程序集里所有 <see cref="IToolboxTool"/> 实现，
/// 但反射<strong>只能拿到类型，拿不到"这个插件想怎么展示自己的工具"</strong>：
/// 分类、排序、展示名、是否需要界面，这些是插件的表达意图，不是运行时能推断出来的。
/// </para>
/// <para>
/// 所以实现本接口，在 <see cref="ProvideTools"/> 里声明；
/// 宿主把它与自动发现的结果按 <see cref="ToolboxToolInfo.Id"/> 合并：
/// <b>声明里的展示信息优先</b>，<c>HasView</c> 以实际实例为准。
/// 声明的顺序仅供参考，宿主最终按 <c>Category → Order → Id</c> 排序。
/// </para>
/// <para>
/// 该方法会在加载期与刷新界面时被调用，请保持<strong>纯粹、无副作用、不耗时</strong>，且只返回静态描述
/// （不要在这里 <c>new</c> 控件，也不要缓存实例）。
/// </para>
/// </remarks>
public interface IToolboxToolProvider
{
    /// <summary>声明本插件提供的工具。</summary>
    IReadOnlyList<ToolboxToolInfo> ProvideTools();
}
