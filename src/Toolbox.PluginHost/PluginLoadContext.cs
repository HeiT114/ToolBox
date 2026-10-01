using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Toolbox.PluginHost;

/// <summary>
/// 每个插件一个<strong>可回收</strong>的 <see cref="AssemblyLoadContext"/>。
/// </summary>
/// <remarks>
/// <para>职责边界：只负责"程序集/原生库怎么找、由谁提供"，不做反射扫描、不做 DI、不做生命周期。</para>
/// <para>
/// 解析顺序（<see cref="Load"/>）：
/// <list type="number">
///   <item>共享清单命中 → 返回 <see langword="null"/>，交给默认 ALC（类型同一性的唯一保障）；</item>
///   <item><see cref="AssemblyDependencyResolver"/> 按插件自己的 <c>*.deps.json</c> 解析私有依赖；</item>
///   <item>同目录同名 dll 兜底（适用于没带 deps.json 的简单插件）；</item>
///   <item>仍然返回 <see langword="null"/>，交给默认 ALC 处理框架程序集。</item>
/// </list>
/// </para>
/// <para>
/// 安全提示：本类不提供沙箱。程序集一旦加载即为完全信任代码，
/// 权限控制要靠进程隔离（另起进程 + 受限账户/作业对象），而不是 ALC。
/// </para>
/// </remarks>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _sharedAssemblies;
    private readonly Lock _sync = new();
    private readonly Action<string>? _trace;

    /// <param name="pluginId">插件标识，仅用于 ALC 名称与日志。</param>
    /// <param name="mainAssemblyPath">入口程序集绝对路径（<see cref="AssemblyDependencyResolver"/> 的锚点）。</param>
    /// <param name="sharedAssemblies">必须由默认 ALC 提供的程序集简单名。</param>
    /// <param name="trace">可选的诊断回调（宿主把它接到 ILogger 上）。</param>
    public PluginLoadContext(
        string pluginId,
        string mainAssemblyPath,
        IEnumerable<string>? sharedAssemblies = null,
        Action<string>? trace = null)
        : base(name: $"ToolboxPlugin:{pluginId}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainAssemblyPath);

        MainAssemblyPath = Path.GetFullPath(mainAssemblyPath);
        PluginDirectory = Path.GetDirectoryName(MainAssemblyPath)
            ?? throw new ArgumentException($"无法从路径推断插件目录：{mainAssemblyPath}", nameof(mainAssemblyPath));

        // ⚠ 必须在加载任何东西之前构造：它读取入口程序集旁边的 *.deps.json，
        //   并据此解析 nuget 依赖、satellite 程序集与 runtimes/<rid>/native。
        _resolver = new AssemblyDependencyResolver(MainAssemblyPath);
        _sharedAssemblies = new HashSet<string>(sharedAssemblies ?? [], StringComparer.OrdinalIgnoreCase);
        _trace = trace;

        if (trace is not null)
        {
            // 只订阅宿主侧的轻量回调：回调里绝不能捕获插件对象，否则等于自己把自己钉住。
            Unloading += _ => trace($"[{Name}] 收到 Unloading 信号（此时只是「请求卸载」，真正的回收要等 GC）");
        }
    }

    public string MainAssemblyPath { get; }

    public string PluginDirectory { get; }

    /// <summary>
    /// 追加共享程序集。用于"插件清单里没写、但插件特性 <c>[ToolboxPlugin(SharedAssemblies=...)]</c> 里写了"的场景。
    /// 必须在依赖它的程序集被加载<strong>之前</strong>调用（约定：紧随入口程序集加载之后）。
    /// </summary>
    public void AddSharedAssemblies(IEnumerable<string> assemblyNames)
    {
        lock (_sync)
        {
            foreach (var name in assemblyNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _sharedAssemblies.Add(name.Trim());
                }
            }
        }
    }

    /// <summary>诊断用：当前 ALC 里已加载的程序集简单名（不含插件对象，不会钉住回收）。</summary>
    public IReadOnlyList<string> GetLoadedAssemblyNames() =>
        [.. Assemblies.Select(a => a.GetName().Name ?? "<unknown>").Order(StringComparer.OrdinalIgnoreCase)];

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // 1) 共享程序集（Toolbox.Contracts 等）：绝不能在这里加载副本，否则跨 ALC 类型不兼容。
        lock (_sync)
        {
            if (_sharedAssemblies.Contains(name))
            {
                _trace?.Invoke($"[{Name}] {name} 命中共享清单 → 默认 ALC");
                return null;
            }
        }

        // 2) 插件私有依赖：严格按插件自己的 deps.json 解析（含版本、satellite、native 描述）。
        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
        {
            _trace?.Invoke($"[{Name}] {name} → {resolved}");
            return LoadFromAssemblyPath(resolved);
        }

        // 3) 兜底：没有 deps.json 的插件，按"同目录同名 dll"探测。
        var probe = Path.Combine(PluginDirectory, name + ".dll");
        if (File.Exists(probe))
        {
            _trace?.Invoke($"[{Name}] {name} → 目录探测 {probe}");
            return LoadFromAssemblyPath(probe);
        }

        // 4) 交回默认 ALC（框架程序集、宿主已加载的程序集走这条路）。
        _trace?.Invoke($"[{Name}] {name} 未在插件内解析到 → 默认 ALC");
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (resolved is not null)
        {
            _trace?.Invoke($"[{Name}] native {unmanagedDllName} → {resolved}");
            return LoadUnmanagedDllFromPath(resolved);
        }

        // 兜底：runtimes/<rid>/native/<name>[.dll]
        var nativeRoot = Path.Combine(PluginDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        string[] candidates =
        [
            Path.Combine(nativeRoot, unmanagedDllName),
            Path.Combine(nativeRoot, unmanagedDllName + ".dll"),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return LoadUnmanagedDllFromPath(candidate);
            }
        }

        _trace?.Invoke($"[{Name}] native {unmanagedDllName} 未解析到 → 交回宿主进程");
        return IntPtr.Zero;
    }

    public override string ToString() => $"{Name} (collectible, dir={PluginDirectory})";
}
