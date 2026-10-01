using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Toolbox.Contracts;

namespace Toolbox.PluginHost;

/// <summary>
/// 单个插件的加载器：来源实体化 → 定位入口 → 建 ALC → 扫描类型 → 组装插件级 DI → 初始化。
/// </summary>
/// <remarks>
/// <para>加载顺序是有讲究的，每一步都对应一类常见故障：</para>
/// <list type="number">
///   <item><b>实体化</b>：目录就地加载；zip 先解包到"按内容哈希命名的影子目录"（Windows 文件锁 + 升级隔离）；</item>
///   <item><b>读清单</b>：<c>plugin.json</c> 提供 Id/入口/共享程序集/默认设置（没有清单时按约定推断）；</item>
///   <item><b>定位入口</b>：清单指定 → <c>&lt;目录名&gt;.dll</c> → 唯一的 dll；多个 dll 且没清单则直接报错；</item>
///   <item><b>建 ALC</b>：<see cref="PluginLoadContext"/>（collectible，且带着共享程序集清单）；</item>
///   <item><b>加载入口程序集 + 扫描</b>：找 <see cref="IToolboxPlugin"/> 与 <see cref="IToolboxTool"/> 实现；</item>
///   <item><b>回填共享清单</b>：特性里声明的共享程序集在这里补进 ALC（必须在依赖加载前）；</item>
///   <item><b>组装插件私有 DI</b>：日志/配置/上下文/插件实例/所有工具都注册进去，<c>ValidateOnBuild</c> 打开；</item>
///   <item><b>初始化</b>：构造插件与工具并调用 <see cref="IToolboxPlugin.InitializeAsync"/>；任一步抛错即整插件失败并清场。</item>
/// </list>
/// <para>本类不持有插件状态；"当前加载了哪些插件"由 <see cref="PluginManager"/> 负责。</para>
/// </remarks>
public sealed class PluginLoader
{
    private static readonly string[] ContractsAssemblyNames = ["Toolbox.Contracts"];

    private readonly PluginHostOptions _options;
    private readonly ILogger<PluginLoader> _logger;
    private readonly IPluginSettingsProvider _settingsProvider;

    public PluginLoader(
        PluginHostOptions options,
        ILogger<PluginLoader> logger,
        IPluginSettingsProvider? settingsProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsProvider = settingsProvider ?? new NullPluginSettingsProvider();
    }

    public PluginHostOptions Options => _options;

    /// <summary>插件根目录（绝对路径）。</summary>
    public string PluginRoot => _options.ResolvePath(_options.PluginDirectory);

    /// <summary>zip 影子解包根目录（绝对路径）。</summary>
    public string ShadowRoot => _options.ResolvePath(_options.ShadowDirectory);

    /// <summary>插件私有数据根目录（绝对路径）。</summary>
    public string DataRoot => _options.ResolvePath(_options.DataDirectory);

    /// <summary>
    /// 只做"发现"：扫 <c>plugins/*/</c> 与 <c>plugins/*.zip</c>，不触碰任何程序集、不产生副作用。
    /// UI 的"可用插件列表"就用它。
    /// </summary>
    public IReadOnlyList<PluginSource> Discover()
    {
        var root = PluginRoot;
        if (!Directory.Exists(root))
        {
            _logger.LogInformation("插件根目录不存在，跳过发现：{Root}", root);
            return [];
        }

        var sources = new List<PluginSource>();

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);

            // 影子目录、隐藏目录一律不算插件
            if (name.StartsWith('.') || (File.GetAttributes(directory) & FileAttributes.Hidden) != 0)
            {
                continue;
            }

            var hasManifest = File.Exists(Path.Combine(directory, PluginManifest.FileName));
            var hasDll = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any();
            if (!hasManifest && !hasDll)
            {
                continue;
            }

            sources.Add(new PluginSource(directory, PluginSourceKind.Directory));
        }

        foreach (var zip in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
        {
            sources.Add(new PluginSource(zip, PluginSourceKind.Zip));
        }

        return [.. sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>按路径加载（目录或 zip）。</summary>
    public Task<PluginHandle> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(PluginSource.Create(path), cancellationToken);

    /// <summary>
    /// 加载一个插件。失败时抛 <see cref="PluginLoadException"/>，并保证已建立的 ALC 与 DI 容器被清理掉。
    /// </summary>
    public async Task<PluginHandle> LoadAsync(PluginSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var contentRoot = Materialize(source);
        var manifest = PluginManifest.TryLoad(contentRoot, warning => _logger.LogWarning("{Warning}", warning));
        var entryAssemblyPath = LocateEntryAssembly(contentRoot, manifest, source);

        var descriptor = new PluginDescriptor
        {
            Id = SanitizeId(manifest?.Id is { Length: > 0 } manifestId ? manifestId : source.Name),
            DisplayName = manifest?.DisplayName ?? source.Name,
            Description = manifest?.Description,
            Version = manifest?.Version,
            Author = manifest?.Author,
            SourcePath = source.Path,
            SourceKind = source.Kind,
            ContentRoot = contentRoot,
            EntryAssemblyPath = entryAssemblyPath,
            State = PluginState.Loading,
        };

        var sharedAssemblies = new HashSet<string>(_options.SharedAssemblies, StringComparer.OrdinalIgnoreCase);
        sharedAssemblies.UnionWith(ContractsAssemblyNames);
        if (manifest?.SharedAssemblies is { } declaredShared)
        {
            sharedAssemblies.UnionWith(declaredShared.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()));
        }

        // ALC 名称只是诊断标签：真正的插件 Id 可能要等读完成特性才知道。
        var loadContext = new PluginLoadContext(source.Name, entryAssemblyPath, sharedAssemblies, Trace);
        ServiceProvider? provider = null;

        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
            var scan = Scan(assembly, source);

            if (manifest?.Id is null or { Length: 0 } && scan.Attribute is { } attribute)
            {
                descriptor.Id = SanitizeId(attribute.Id);
                descriptor.DisplayName = manifest?.DisplayName ?? attribute.DisplayName ?? descriptor.Id;
                descriptor.Description ??= attribute.Description;
                descriptor.Version ??= attribute.Version;
                descriptor.Author ??= attribute.Author;

                // 特性里声明的共享程序集：必须在任何依赖它解析之前补进去（此刻入口程序集刚加载完）。
                loadContext.AddSharedAssemblies(attribute.SharedAssemblies);
                _logger.LogDebug("插件 {PluginId} 在特性里声明了共享程序集：{Shared}", descriptor.Id, string.Join(", ", attribute.SharedAssemblies));
            }

            descriptor.DisplayName = manifest?.DisplayName ?? descriptor.DisplayName;
            descriptor.Version ??= assembly.GetName().Version?.ToString();

            var (services, pluginContext) = BuildPluginServices(descriptor, scan, contentRoot, manifest);
            provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            pluginContext.AttachServices(provider);

            // 立即构造：工具或插件构造失败应当在"加载"阶段暴露，而不是等用户点执行。
            var tools = provider.GetServices<IToolboxTool>().ToArray();
            var plugin = (IToolboxPlugin)provider.GetRequiredService(scan.PluginType);

            await plugin.InitializeAsync(pluginContext, cancellationToken).ConfigureAwait(false);

            descriptor.ToolNames = [.. tools.Select(t => t.Name)];
            descriptor.LoadedAssemblyNames = loadContext.GetLoadedAssemblyNames();
            descriptor.State = PluginState.Loaded;
            descriptor.Error = null;
            descriptor.LoadedAt = DateTimeOffset.Now;

            _logger.LogInformation(
                "插件 {PluginId} v{Version} 已加载：{ToolCount} 个工具，ALC 内程序集 {Assemblies}",
                descriptor.Id,
                descriptor.Version,
                tools.Length,
                string.Join(", ", descriptor.LoadedAssemblyNames));

            return new PluginHandle(descriptor, loadContext, assembly, pluginContext, provider, plugin, tools);
        }
        catch (Exception ex)
        {
            // 失败路径同样要清场：否则"反复重试加载坏插件"会一次次漏掉 ALC。
            if (provider is not null)
            {
                try
                {
                    await provider.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeError)
                {
                    _logger.LogWarning(disposeError, "释放插件 {PluginId} 的 DI 容器时出错。", descriptor.Id);
                }
            }

            loadContext.Unload();

            descriptor.State = PluginState.Failed;
            descriptor.Error = ex.Message;
            descriptor.ToolNames = [];
            descriptor.LoadedAssemblyNames = [];

            var reason = ex is PluginLoadException ? ex.Message : $"插件 {descriptor.Id} 加载失败：{ex.Message}";
            _logger.LogError(ex, "插件 {PluginId} 加载失败（来源 {Source}）。", descriptor.Id, source.Path);
            throw new PluginLoadException(reason, ex) { SourcePath = source.Path, Descriptor = descriptor };
        }
    }

    /// <summary>把来源变成"可以直接加载的内容根目录"。</summary>
    private string Materialize(PluginSource source) => source.Kind switch
    {
        PluginSourceKind.Directory when Directory.Exists(source.Path) => source.Path,
        PluginSourceKind.Directory => throw new PluginLoadException($"插件目录不存在：{source.Path}") { SourcePath = source.Path },
        PluginSourceKind.Zip when File.Exists(source.Path) => PluginPackageExtractor.Extract(source.Path, ShadowRoot),
        PluginSourceKind.Zip => throw new PluginLoadException($"插件包不存在：{source.Path}") { SourcePath = source.Path },
        _ => throw new PluginLoadException($"不支持的插件来源：{source.Path}") { SourcePath = source.Path },
    };

    /// <summary>定位入口程序集：清单 > 目录同名 dll > 唯一 dll。</summary>
    private static string LocateEntryAssembly(string contentRoot, PluginManifest? manifest, PluginSource source)
    {
        if (manifest?.EntryAssembly is { Length: > 0 } declaredEntry)
        {
            var candidate = Path.GetFullPath(Path.Combine(contentRoot, declaredEntry));
            if (!File.Exists(candidate))
            {
                throw new PluginLoadException($"plugin.json 指定的入口程序集不存在：{declaredEntry}") { SourcePath = source.Path };
            }

            return candidate;
        }

        var conventional = Path.Combine(contentRoot, Path.GetFileName(contentRoot) + ".dll");
        if (File.Exists(conventional))
        {
            return conventional;
        }

        // 排除宿主共享的契约 dll：它出现在插件目录里本身就是错误打包，不能当作入口。
        var candidates = Directory
            .GetFiles(contentRoot, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(file => !ContractsAssemblyNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new PluginLoadException($"插件目录里没有找到任何程序集：{contentRoot}") { SourcePath = source.Path },
            _ => throw new PluginLoadException(
                $"插件 {source.Name} 里有多个程序集，无法确定入口；请在 plugin.json 里声明 entryAssembly。候选：{string.Join(", ", candidates.Select(Path.GetFileName))}")
            { SourcePath = source.Path },
        };
    }

    /// <summary>扫描入口程序集：找插件入口类型与全部工具类型。</summary>
    private PluginScanResult Scan(Assembly assembly, PluginSource source)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 缺依赖时 GetTypes 会整体失败，但已经加载好的类型仍可用：能加载多少用多少。
            types = [.. ex.Types.OfType<Type>()];
            var loaderErrors = ex.LoaderExceptions.OfType<Exception>().Select(e => e.Message).Distinct();
            _logger.LogWarning(
                "程序集 {Assembly} 有类型无法加载（通常是缺依赖）：{Errors}",
                assembly.GetName().Name,
                string.Join("; ", loaderErrors));
        }

        var pluginTypes = types
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IToolboxPlugin).IsAssignableFrom(t))
            .ToArray();

        if (pluginTypes.Length == 0)
        {
            throw new PluginLoadException($"程序集 {assembly.GetName().Name} 里没有找到 IToolboxPlugin 实现。") { SourcePath = source.Path };
        }

        var attributed = pluginTypes
            .Select(t => (Type: t, Attribute: t.GetCustomAttribute<ToolboxPluginAttribute>()))
            .Where(pair => pair.Attribute is not null)
            .ToArray();

        var (entryType, entryAttribute) = attributed.Length switch
        {
            1 => (attributed[0].Type, attributed[0].Attribute),
            0 when pluginTypes.Length == 1 => (pluginTypes[0], null),
            0 => throw new PluginLoadException(
                $"程序集 {assembly.GetName().Name} 里有 {pluginTypes.Length} 个 IToolboxPlugin 实现，但没有 [ToolboxPlugin] 特性指明入口。")
            { SourcePath = source.Path },
            _ => throw new PluginLoadException(
                $"程序集 {assembly.GetName().Name} 里有多个带 [ToolboxPlugin] 的类型，一个程序集只允许一个插件入口。")
            { SourcePath = source.Path },
        };

        var toolTypes = types
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true, IsGenericTypeDefinition: false }
                        && typeof(IToolboxTool).IsAssignableFrom(t))
            .ToArray();

        foreach (var toolType in toolTypes)
        {
            if (toolType.GetConstructors().Length == 0)
            {
                throw new PluginLoadException($"工具 {toolType.FullName} 没有公开构造函数，无法由 DI 构造。") { SourcePath = source.Path };
            }
        }

        return new PluginScanResult(entryType, entryAttribute, toolTypes);
    }

    /// <summary>
    /// 组装"插件私有"DI 容器 —— 宿主容器与插件容器的边界就在这里。
    /// 插件只能看到 <see cref="IPluginContext"/>、<see cref="IPluginLogger"/>、<see cref="IPluginConfiguration"/>、
    /// 自己的入口类型和工具类型；反过来宿主也不会把插件类型注册进自己的容器。
    /// </summary>
    private (ServiceCollection Services, HostPluginContext Context) BuildPluginServices(
        PluginDescriptor descriptor,
        PluginScanResult scan,
        string contentRoot,
        PluginManifest? manifest)
    {
        var dataDirectory = EnsureDataDirectory(descriptor.Id);
        var pluginLogger = new PluginLoggerAdapter(_logger, descriptor.Id);
        var configuration = new PluginConfigurationAdapter(BuildSettings(manifest, descriptor.Id));
        var pluginContext = new HostPluginContext(descriptor.Id, contentRoot, dataDirectory, pluginLogger, configuration);

        var services = new ServiceCollection();

        services.AddSingleton<IPluginLogger>(pluginLogger);
        services.AddSingleton<IPluginConfiguration>(configuration);
        services.AddSingleton<IPluginContext>(pluginContext);

        // 插件入口与全部工具都注册为单例：生命周期 = 插件加载生命周期，
        // 卸载时随 ServiceProvider.DisposeAsync 一起释放。
        services.AddSingleton(scan.PluginType);
        foreach (var toolType in scan.ToolTypes)
        {
            services.AddSingleton(typeof(IToolboxTool), toolType);
        }

        return (services, pluginContext);
    }

    private void Trace(string message) => _logger.LogDebug("{Message}", message);

    private string EnsureDataDirectory(string pluginId)
    {
        var directory = Path.Combine(DataRoot, SanitizeId(pluginId));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private Dictionary<string, string?> BuildSettings(PluginManifest? manifest, string pluginId)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (manifest?.Settings is { } defaults)
        {
            foreach (var (key, value) in defaults)
            {
                settings[key] = value;
            }
        }

        // 主程序配置覆盖插件自带的默认值
        foreach (var (key, value) in _settingsProvider.GetSettings(pluginId))
        {
            settings[key] = value;
        }

        return settings;
    }

    private static string SanitizeId(string id)
    {
        var trimmed = id.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(trimmed.Select(c => invalid.Contains(c) || c == Path.DirectorySeparatorChar ? '_' : c));

        if (safe.Length == 0 || safe is "." or "..")
        {
            throw new PluginLoadException($"插件 Id 非法：{id}");
        }

        return safe;
    }

    private sealed record PluginScanResult(
        Type PluginType,
        ToolboxPluginAttribute? Attribute,
        IReadOnlyList<Type> ToolTypes);
}
