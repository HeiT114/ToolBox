using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Toolbox.Contracts;

namespace Toolbox.PluginHost;

/// <summary>
/// 一个"活着的插件"：ALC + 入口程序集 + 插件私有 DI 容器 + 插件实例 + 工具实例。
/// </summary>
/// <remarks>
/// <para>
/// <strong>这是一个"用完即弃"的对象</strong>。宿主只能在"当前正加载着这个插件"这段时间内持有它；
/// 卸载时必须把引用丢掉，否则句柄里的 <c>Assembly</c> / 插件实例会永远钉住 ALC。
/// </para>
/// <para>
/// 因此：不要把 <see cref="PluginHandle"/>、<see cref="Plugin"/>、<see cref="Tools"/> 里的对象
/// 存进宿主的长期缓存、静态字段、DI 单例或 UI 绑定源。UI 需要展示的信息请只取
/// <see cref="Descriptor"/>（纯字符串）。
/// </para>
/// </remarks>
public sealed class PluginHandle
{
    private readonly PluginDescriptor _descriptor;

    // 下面这些字段在卸载时会被逐个置空 —— 置空本身就是卸载协议的一部分。
    private PluginLoadContext? _context;
    private Assembly? _entryAssembly;
    private ServiceProvider? _services;
    private IToolboxPlugin? _plugin;
    private IReadOnlyList<IToolboxTool>? _tools;
    private HostPluginContext? _pluginContext;

    internal PluginHandle(
        PluginDescriptor descriptor,
        PluginLoadContext context,
        Assembly entryAssembly,
        HostPluginContext pluginContext,
        ServiceProvider services,
        IToolboxPlugin plugin,
        IReadOnlyList<IToolboxTool> tools)
    {
        _descriptor = descriptor;
        _context = context;
        _entryAssembly = entryAssembly;
        _pluginContext = pluginContext;
        _services = services;
        _plugin = plugin;
        _tools = tools;
    }

    public PluginDescriptor Descriptor => _descriptor;

    public PluginLoadContext Context => _context ?? throw Detached();

    public Assembly EntryAssembly => _entryAssembly ?? throw Detached();

    public IToolboxPlugin Plugin => _plugin ?? throw Detached();

    /// <summary>本插件提供的工具实例（随插件 DI 容器一起释放）。</summary>
    public IReadOnlyList<IToolboxTool> Tools => _tools ?? throw Detached();

    /// <summary>
    /// 插件环境入口。宿主侧 UI 适配层创建视图时要用它（<see cref="IToolboxToolView.CreateView"/>）。
    /// </summary>
    /// <remarks>
    /// ⚠ 这个对象向上牵着插件的 DI 容器与插件实例：**只能即用即弃**，
    /// 不要缓存到宿主的长生命周期对象里，否则回收验证会失败。
    /// </remarks>
    public IPluginContext PluginContext => _pluginContext ?? throw Detached();

    public bool IsDetached => _context is null;

    public IToolboxTool? FindTool(string name) =>
        Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 关闭 + 断开 + 请求卸载，返回一个只指向 ALC 的 <see cref="WeakReference"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三步顺序不可颠倒：
    /// <list type="number">
    ///   <item>先让插件自己清理（<c>ShutdownAsync</c>：退订事件、停后台线程）；</item>
    ///   <item>再释放插件私有 DI 容器（容器里的单例——包括插件实例本身和工具——在这里被 Dispose）；</item>
    ///   <item>最后 <c>Unload()</c>，并确保句柄里的所有强引用都已置空。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 本方法是<strong>同步</strong>的，且刻意不跨 await：卸载路径上任何 async 状态机都可能把插件对象
    /// 留在堆上，让"回收验证"永远失败。等待异步清理的代价由这里显式阻塞承担（调用方在后台线程执行）。
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static PluginDetachResult ShutdownAndDetach(
        PluginHandle handle,
        CancellationToken cancellationToken,
        Action<Exception>? onError)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var context = handle._context
            ?? throw new InvalidOperationException($"插件 {handle._descriptor.Id} 已经卸载。");

        // ⚠ 先建 WeakReference：Unload() 之后就只剩它还能"观察"这个 ALC。
        var weakContext = new WeakReference(context, trackResurrection: false);

        // 探针必须在 teardown 之前建立（teardown 会把句柄里的强引用逐个置空）。
        var probes = handle.CreateLivenessProbes();

        handle.Teardown(cancellationToken, onError);
        context.Unload();

        return new PluginDetachResult(weakContext, probes);
    }

    /// <summary>
    /// 为"插件侧对象"建立弱引用探针：回收失败时用来回答"到底是谁还活着"。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private IReadOnlyList<PluginLivenessProbe> CreateLivenessProbes()
    {
        var probes = new List<PluginLivenessProbe>(8);

        if (_plugin is not null)
        {
            probes.Add(new PluginLivenessProbe($"插件实例 {_plugin.GetType().FullName}", new WeakReference(_plugin)));
        }

        if (_tools is not null)
        {
            foreach (var tool in _tools)
            {
                probes.Add(new PluginLivenessProbe($"工具 {tool.GetType().FullName}", new WeakReference(tool)));
            }
        }

        if (_services is not null)
        {
            probes.Add(new PluginLivenessProbe($"插件DI容器 {_services.GetType().FullName}", new WeakReference(_services)));
        }

        if (_pluginContext is not null)
        {
            probes.Add(new PluginLivenessProbe($"插件上下文 {_pluginContext.GetType().FullName}", new WeakReference(_pluginContext)));
        }

        if (_entryAssembly is not null)
        {
            probes.Add(new PluginLivenessProbe($"入口程序集 {_entryAssembly.GetName().Name}", new WeakReference(_entryAssembly)));
        }

        return probes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Teardown(CancellationToken cancellationToken, Action<Exception>? onError)
    {
        var plugin = _plugin;
        var services = _services;

        if (plugin is not null)
        {
            try
            {
                AwaitSync(plugin.ShutdownAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                onError?.Invoke(new PluginLoadException($"插件 {_descriptor.Id} 的 ShutdownAsync 抛出异常（继续卸载）。", ex));
            }
        }

        if (services is not null)
        {
            try
            {
                // DisposeAsync 会顺带 Dispose 容器里的单例（插件实例、工具）。
                // 用 DisposeAsync 而不是 Dispose：只实现 IAsyncDisposable 的单例在同步 Dispose 时会抛异常。
                AwaitSync(services.DisposeAsync());
            }
            catch (Exception ex)
            {
                onError?.Invoke(new PluginLoadException($"插件 {_descriptor.Id} 的 DI 容器释放失败。", ex));
            }
        }

        // 断开所有强引用 —— 这一步做完，宿主侧除了 WeakReference 之外不再有任何东西指向插件。
        _plugin = null;
        _tools = null;
        _entryAssembly = null;
        _services = null;
        _pluginContext?.Detach();
        _pluginContext = null;
        _context = null;
    }

    /// <summary>把 ValueTask 同步等到完成；已经完成时不产生任何额外分配与状态机。</summary>
    private static void AwaitSync(ValueTask valueTask)
    {
        if (valueTask.IsCompletedSuccessfully)
        {
            valueTask.GetAwaiter().GetResult();
            return;
        }

        valueTask.AsTask().GetAwaiter().GetResult();
    }

    private ObjectDisposedException Detached() =>
        new(_descriptor.Id, $"插件 {_descriptor.Id} 已卸载，句柄失效。");

    public override string ToString() => $"PluginHandle({_descriptor})";
}

/// <summary>卸载（detach）的产物：只包含"观察用"的弱引用，不含任何插件强引用。</summary>
/// <param name="Context">被 Unload 的 ALC 的弱引用。</param>
/// <param name="Probes">插件侧对象的存活探针。</param>
internal readonly record struct PluginDetachResult(WeakReference Context, IReadOnlyList<PluginLivenessProbe> Probes);
