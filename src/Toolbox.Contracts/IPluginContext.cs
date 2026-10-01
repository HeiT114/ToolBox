namespace Toolbox.Contracts;

/// <summary>
/// 宿主交给插件的唯一环境入口（"插件能看到的世界"）。
/// 契约层不引入 Microsoft.Extensions.*，因此这里用 BCL 的 <see cref="IServiceProvider"/> 暴露 DI，
/// 用 <see cref="IPluginLogger"/> / <see cref="IPluginConfiguration"/> 暴露日志与配置。
/// </summary>
public interface IPluginContext
{
    /// <summary>插件稳定标识（来自 <see cref="ToolboxPluginAttribute.Id"/> 或清单）。</summary>
    string PluginId { get; }

    /// <summary>插件自身的安装目录（目录插件即原目录；zip 插件为只读的影子解包目录）。</summary>
    string PluginDirectory { get; }

    /// <summary>插件私有可写目录，保证已创建。卸载/升级不会删除它。</summary>
    string DataDirectory { get; }

    /// <summary>
    /// 插件<strong>私有</strong> DI 容器。宿主已经把插件里的所有 <see cref="IToolboxTool"/> 实现
    /// 以及 <see cref="IPluginLogger"/> / <see cref="IPluginConfiguration"/> / 本接口注册进去，
    /// 插件可以在 <see cref="IToolboxPlugin.InitializeAsync"/> 里用构造注入或
    /// <c>GetRequiredService</c> 取用。
    /// </summary>
    /// <remarks>
    /// 该容器在卸载时会被 Dispose；<strong>不要</strong>把这里解析出来的对象注册回宿主容器，
    /// 那会把插件类型钉在默认 ALC 上。
    /// </remarks>
    IServiceProvider Services { get; }

    IPluginLogger Logger { get; }

    IPluginConfiguration Configuration { get; }
}
