namespace Toolbox.Contracts;

/// <summary>
/// "插件提供哪些工具"的<strong>中立声明</strong>：全是 BCL 类型，不包含视图、不包含插件实例。
/// </summary>
/// <remarks>
/// 宿主拿到它就能画工具列表（分类/排序/是否有界面），无需实例化插件对象，
/// 也不会因此持有任何跨 ALC 的引用——这类结构可以安全地缓存到 UI。
/// </remarks>
/// <param name="Id">工具标识（与 <see cref="IToolboxTool.Name"/> / <see cref="IToolboxToolView.Id"/> 一致）。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="Description">一句话描述。</param>
/// <param name="Category">分类，宿主用来分组。</param>
/// <param name="Order">同分类内排序，越小越靠前。</param>
/// <param name="HasView">是否提供界面（最终以实际工具实例是否实现 <see cref="IToolboxToolView"/> 为准）。</param>
public sealed record ToolboxToolInfo(
    string Id,
    string DisplayName,
    string? Description = null,
    string? Category = null,
    int Order = 0,
    bool HasView = false);
