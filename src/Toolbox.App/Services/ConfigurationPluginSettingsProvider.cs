using Microsoft.Extensions.Configuration;
using Toolbox.PluginHost;

namespace Toolbox.App.Services;

/// <summary>
/// 把主程序配置里的 <c>PluginHost:Plugins:&lt;pluginId&gt;</c> 节点喂给插件。
/// </summary>
/// <remarks>
/// 这一层就是"配置由主程序负责"的落点：宿主层只认 <see cref="IPluginSettingsProvider"/>，
/// 完全不知道配置来自 JSON、数据库还是界面。
/// 形如
/// <code>
/// "PluginHost": {
///   "Plugins": {
///     "clock": { "format": "yyyy/MM/dd" }
///   }
/// }
/// </code>
/// 的配置会覆盖插件自己 <c>plugin.json</c> 里的默认值。
/// </remarks>
internal sealed class ConfigurationPluginSettingsProvider(IConfiguration configuration) : IPluginSettingsProvider
{
    public IReadOnlyDictionary<string, string?> GetSettings(string pluginId)
    {
        var section = configuration.GetSection($"PluginHost:Plugins:{pluginId}");
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // 只处理一层扁平键；需要嵌套结构的插件请自己解析 DataDirectory 下的配置文件。
        foreach (var child in section.GetChildren())
        {
            result[child.Key] = child.Value;
        }

        return result;
    }
}
