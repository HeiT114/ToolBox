using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Toolbox.PluginHost;

/// <summary>
/// 宿主服务的 DI 注册入口。
/// </summary>
/// <remarks>
/// 这里用 C# 14 的 <b>extension members</b>（<c>extension(...)</c> 块）而不是传统的静态扩展方法：
/// 同一个接收者上的扩展成员可以像实例成员一样分组、复用泛型参数，读起来更接近"给 IServiceCollection 加 API"。
/// </remarks>
public static class PluginHostServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// 注册插件宿主需要的一切：<see cref="PluginHostOptions"/>、<see cref="PluginLoader"/>、<see cref="PluginManager"/>。
        /// </summary>
        /// <param name="configure">配置回调（通常来自主程序配置文件的 <c>PluginHost</c> 节点）。</param>
        public IServiceCollection AddToolboxPluginHost(Action<PluginHostOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var options = new PluginHostOptions();
            configure?.Invoke(options);

            services.TryAddSingleton(options);
            services.TryAddSingleton<IPluginSettingsProvider, NullPluginSettingsProvider>();
            services.TryAddSingleton<PluginLoader>();
            services.TryAddSingleton<PluginManager>();

            return services;
        }
    }
}
