namespace Toolbox.Contracts;

/// <summary>
/// "工具"契约：工具箱里真正干活的最小单元。一个插件可以提供 0..N 个工具。
/// </summary>
/// <remarks>
/// 宿主通过反射发现插件程序集中所有公开、非抽象的 <see cref="IToolboxTool"/> 实现，
/// 并注册进<strong>插件私有</strong> DI 容器（因此工具可以构造注入任何插件内部服务）。
/// 工具实例的生命周期 = 插件加载周期，卸载时随容器一起释放。
/// </remarks>
public interface IToolboxTool
{
    /// <summary>工具名，在插件内唯一（宿主界面按 <c>插件Id/工具名</c> 展示）。</summary>
    string Name { get; }

    /// <summary>一句话描述。</summary>
    string? Description { get; }

    /// <summary>
    /// 执行工具。<paramref name="arguments"/> 是宿主传来的扁平参数表
    /// （命令行 <c>--key=value</c> 或 UI 表单都归一到它）。
    /// </summary>
    ValueTask<ToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>工具执行结果。刻意保持 POCO：跨 ALC 传递的只能是契约层类型或 BCL 类型。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Output">成功时的输出（可为空）。</param>
/// <param name="Error">失败原因（可为空）。</param>
public readonly record struct ToolResult(bool Success, string? Output, string? Error)
{
    public static ToolResult Ok(string? output = null) => new(true, output, null);

    public static ToolResult Fail(string error) => new(false, null, error);
}
