namespace Toolbox.PluginHost;

/// <summary>
/// 创建插件视图的结果。
/// </summary>
/// <remarks>
/// <see cref="View"/> 是<strong>插件 ALC 里的对象</strong>：宿主/适配层可以把它挂到 UI 上，
/// 但必须保证在卸载该插件之前把它从视觉树上摘下来并丢弃引用，否则回收验证必然失败。
/// </remarks>
public sealed record ToolViewCreationResult(bool Success, object? View, string? Error)
{
    public static ToolViewCreationResult Ok(object view) => new(true, view, null);

    public static ToolViewCreationResult Fail(string error) => new(false, null, error);
}
