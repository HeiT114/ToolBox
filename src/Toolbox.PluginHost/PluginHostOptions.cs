namespace Toolbox.PluginHost;

/// <summary>
/// 插件宿主配置。可以直接绑定主程序配置文件的 <c>PluginHost</c> 节点
/// （<c>configuration.GetSection("PluginHost").Get&lt;PluginHostOptions&gt;()</c>）。
/// </summary>
public sealed class PluginHostOptions
{
    /// <summary>
    /// 插件根目录（相对路径按 <see cref="AppContext.BaseDirectory"/> 解析）。
    /// 目录下可以放：每个子目录一个"目录插件"，以及任意多个 <c>*.zip</c> 插件包。
    /// </summary>
    /// <remarks>这里用 C# 14 的 <c>field</c> 关键字直接在访问器里做校验，避免"半初始化"的配置对象。</remarks>
    public string PluginDirectory
    {
        get => field;
        set => field = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("PluginDirectory 不能为空。", nameof(value))
            : value.Trim();
    } = "plugins";

    /// <summary>zip 插件的影子解包根目录（按内容哈希分目录，升级不会覆盖旧版本）。</summary>
    public string ShadowDirectory { get; set; } = "plugins/.shadow";

    /// <summary>插件私有可写数据根目录，每个插件一个子目录（卸载/升级不清除）。</summary>
    public string DataDirectory { get; set; } = "plugin-data";

    /// <summary>
    /// 强制由默认 ALC 解析的程序集简单名。这份清单决定"谁与宿主共享类型"。
    /// <c>Toolbox.Contracts</c> 必须永远在里面，否则插件里的 <c>IToolboxPlugin</c>
    /// 与宿主看到的会是两个不同的类型。
    /// </summary>
    public string[] SharedAssemblies { get; set; } = ["Toolbox.Contracts"];

    /// <summary>不参与加载的插件 Id（主程序"禁用插件"功能的落点）。</summary>
    public string[] DisabledPlugins { get; set; } = [];

    /// <summary>卸载后是否做 ALC 回收验证（WeakReference + 强制 GC）。生产可关，诊断时必开。</summary>
    public bool VerifyUnload { get; set; } = true;

    public int UnloadVerifyAttempts
    {
        get => field;
        set => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "UnloadVerifyAttempts 必须为正数。");
    } = 8;

    /// <summary>把配置里的相对路径还原成绝对路径。</summary>
    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
}
