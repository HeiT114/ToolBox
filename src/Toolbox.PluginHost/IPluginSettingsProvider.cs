namespace Toolbox.PluginHost;

/// <summary>
/// 插件级配置来源：主程序实现它（例如读 <c>PluginHost:Plugins:&lt;pluginId&gt;</c> 配置节），
/// 宿主只认这个接口，因此 <c>Toolbox.PluginHost</c> 不需要依赖任何配置包。
/// </summary>
public interface IPluginSettingsProvider
{
    /// <summary>取某个插件的配置键值（扁平）。没有配置时返回空字典，不要返回 <see langword="null"/>。</summary>
    IReadOnlyDictionary<string, string?> GetSettings(string pluginId);
}

/// <summary>默认实现：没有任何外部配置。插件只能看到自己 <c>plugin.json</c> 里的 default settings。</summary>
public sealed class NullPluginSettingsProvider : IPluginSettingsProvider
{
    private static readonly Dictionary<string, string?> Empty = [];

    public IReadOnlyDictionary<string, string?> GetSettings(string pluginId) => Empty;
}
