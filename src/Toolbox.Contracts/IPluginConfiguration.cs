namespace Toolbox.Contracts;

/// <summary>
/// 插件只读配置视图：来源是"主程序配置文件的 PluginHost:Plugins:&lt;pluginId&gt; 节点"
/// 与"插件目录下 plugin.json 的 settings 节点"合并后的扁平键值表（后者优先级更低）。
/// </summary>
/// <remarks>
/// 只读 + 扁平，是刻意的：插件配置的写回、校验、UI 呈现属于主程序的职责范围。
/// 需要结构化配置的插件请自行用 System.Text.Json 解析自己的配置文件，路径放在
/// <see cref="IPluginContext.DataDirectory"/> 下。
/// </remarks>
public interface IPluginConfiguration
{
    /// <summary>取字符串值，不存在返回 <see langword="null"/>。</summary>
    string? Get(string key);

    /// <summary>取字符串值，不存在返回 <see langword="false"/>。</summary>
    bool TryGet(string key, out string? value);

    /// <summary>当前插件的全部可见键值（键为配置路径，如 <c>format</c> 或 <c>ui:theme</c>）。</summary>
    IReadOnlyDictionary<string, string?> GetAll();
}
