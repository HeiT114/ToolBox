namespace Toolbox.Contracts;

/// <summary>
/// 标记插件入口类型，并在"不实例化插件"的前提下暴露元数据。
/// 宿主扫描插件程序集时优先读取该特性，缺少特性时会退化为"类型全名 + 程序集版本"。
/// </summary>
/// <param name="id">
/// 稳定标识（建议 reverse-DNS，如 <c>acme.toolbox.clock</c>）。
/// 卸载、重载、按插件隔离的配置与数据目录都以它为准，一旦发布不要再改。
/// </param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ToolboxPluginAttribute(string id) : Attribute
{
    public string Id { get; } = string.IsNullOrWhiteSpace(id)
        ? throw new ArgumentException("插件 Id 不能为空。", nameof(id))
        : id;

    /// <summary>UI 展示名，缺省时回落到 <see cref="Id"/>。</summary>
    public string? DisplayName { get; set; }

    /// <summary>一句话描述，UI 用。</summary>
    public string? Description { get; set; }

    /// <summary>语义化版本，如 <c>1.2.0</c>。缺省时回落到入口程序集版本。</summary>
    public string? Version { get; set; }

    public string? Author { get; set; }

    /// <summary>
    /// 需要由"默认 ALC"提供的程序集简单名（额外追加到宿主全局共享清单）。
    /// 典型场景：插件与宿主共同引用的某个领域模型包，必须保持类型同一性。
    /// 注意：<c>Toolbox.Contracts</c> 永远由宿主强制共享，无需在此重复声明。
    /// </summary>
    public string[] SharedAssemblies { get; set; } = [];
}
