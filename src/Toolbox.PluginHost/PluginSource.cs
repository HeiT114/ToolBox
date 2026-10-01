namespace Toolbox.PluginHost;

/// <summary>插件来源形态。</summary>
public enum PluginSourceKind
{
    /// <summary><c>plugins/&lt;name&gt;/</c>：直接就地加载（开发期最方便，但文件被占用后无法热替换）。</summary>
    Directory = 0,

    /// <summary><c>plugins/&lt;name&gt;.zip</c>：解包到影子目录后加载（发布形态，支持升级/回滚）。</summary>
    Zip = 1,
}

/// <summary>一个可加载的插件来源（发现阶段产物，尚未触碰任何程序集）。</summary>
/// <param name="Path">目录路径或 zip 文件路径（绝对路径）。</param>
/// <param name="Kind">来源形态。</param>
public sealed record PluginSource(string Path, PluginSourceKind Kind)
{
    /// <summary>按路径形态推断来源；<c>.zip</c> 视为包，其余视为目录。</summary>
    public static PluginSource Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        return new PluginSource(
            full,
            full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? PluginSourceKind.Zip : PluginSourceKind.Directory);
    }

    /// <summary>
    /// 来源名：目录插件取目录名（<c>Toolbox.Plugin.Hello</c>），zip 包取去掉扩展名的文件名。
    /// 注意目录不能用 <c>GetFileNameWithoutExtension</c>，否则 <c>Toolbox.Plugin.Hello</c> 会被误判成扩展名。
    /// </summary>
    public string Name => Kind == PluginSourceKind.Zip
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : System.IO.Path.GetFileName(
            Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
}
