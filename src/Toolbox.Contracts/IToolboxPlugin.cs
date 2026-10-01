namespace Toolbox.Contracts;

/// <summary>
/// 插件入口契约。一个插件程序集通常只有一个带 <see cref="ToolboxPluginAttribute"/> 的公开实现。
/// </summary>
/// <remarks>
/// 生命周期由宿主保证：<see cref="InitializeAsync"/> 成功返回后插件才被认为"已加载"；
/// <see cref="ShutdownAsync"/> 在 ALC 卸载<strong>之前</strong>调用，用来退订事件、停止后台线程、
/// 释放非托管资源。这两个方法都不应长时间阻塞——它们是 UI 线程上的协作点。
/// </remarks>
public interface IToolboxPlugin
{
    /// <summary>
    /// 插件装配点：读取配置、订阅宿主事件。不要在这里启动无限循环，
    /// 需要用后台任务时请保存 <see cref="CancellationToken"/> 派生出来的 token 并在 Shutdown 里取消。
    /// </summary>
    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// 卸载前的清理点。实现必须保证调用后本插件不再持有任何能被宿主继续访问的引用，
    /// 否则会钉住整个 <c>AssemblyLoadContext</c>，导致卸载失效。
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
