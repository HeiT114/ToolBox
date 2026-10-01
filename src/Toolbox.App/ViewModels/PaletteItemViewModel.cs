namespace Toolbox.App.ViewModels;

/// <summary>Ctrl+K 命令面板里的一行（一个可执行动作）。</summary>
/// <remarks>纯字符串 + 委托，不引用任何插件对象。</remarks>
public sealed class PaletteItemViewModel(string title, string subtitle, string searchText, Func<Task> action)
{
    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    /// <summary>过滤命中用的合并文本（标题 + 说明 + 插件 Id）。</summary>
    public string SearchText { get; } = searchText;

    /// <summary>执行（执行前面板已经关闭）。</summary>
    public Func<Task> Action { get; } = action;

    /// <summary>是否匹配当前搜索词。</summary>
    public bool Matches(string query) =>
        string.IsNullOrWhiteSpace(query) ||
        SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
}
