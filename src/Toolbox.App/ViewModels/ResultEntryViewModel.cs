namespace Toolbox.App.ViewModels;

/// <summary>工具/操作的执行结果历史里的一行。</summary>
/// <remarks>
/// 只保存字符串投影（时间、标题、正文），不引用任何插件对象 —— 与其它 ViewModel 同一条纪律。
/// </remarks>
public sealed class ResultEntryViewModel(string title, string detail, bool success, string? fullText = null)
{
    /// <summary>触发时刻（HH:mm:ss）。</summary>
    public string Time { get; } = DateTime.Now.ToString("HH:mm:ss");

    /// <summary>短标题（插件/操作名）。</summary>
    public string Title { get; } = title;

    /// <summary>一行摘要（超长截断）。</summary>
    public string Detail { get; } = Truncate(detail);

    /// <summary>完整正文（复制时用）。</summary>
    public string FullText { get; } = string.IsNullOrWhiteSpace(fullText) ? detail : fullText;

    /// <summary>是否成功（✔ / ✘ 与色调）。</summary>
    public bool Success { get; } = success;

    /// <summary>时间 + 标题（列表头一行）。</summary>
    public string Headline => $"{Time} · {Title}";

    /// <summary>复制按钮的 UIA 名称。</summary>
    public string CopyLabel => $"复制 {Title}";

    private static string Truncate(string text)
    {
        var compact = text.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 160 ? compact : compact[..160] + "…";
    }
}
