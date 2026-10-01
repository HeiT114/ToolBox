using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Toolbox.Contracts;

namespace Toolbox.PluginHost;

/// <summary>
/// 插件集合的管理者：目录刷新、加载/卸载/重载、工具调用、影子目录清理。
/// 主程序（UI）只跟它打交道。
/// </summary>
/// <remarks>
/// <para>
/// 线程模型：所有会改变插件集合的操作都串行化在一把 <see cref="SemaphoreSlim"/> 上；
/// 卸载时的 Teardown 与 GC 验证跑在线程池上。<see cref="DescriptorChanged"/> 可能在线程池线程触发，
/// UI 订阅方需要自己 marshal 回 UI 线程。
/// </para>
/// <para>
/// 卸载协议（本类的核心不变量）：
/// <c>从字典摘除 → 同步 Teardown（Shutdown + 容器释放 + ALC.Unload）→ 断开句柄引用 → GC 验证</c>。
/// 中间不允许出现"跨 await 持有句柄"的流程，否则 async 状态机会把插件对象钉在堆上，让回收永远失败。
/// </para>
/// </remarks>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly PluginLoader _loader;
    private readonly PluginHostOptions _options;
    private readonly ILogger<PluginManager> _logger;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, PluginHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginDescriptor> _descriptors = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public PluginManager(PluginLoader loader, PluginHostOptions options, ILogger<PluginManager> logger)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>任何描述变化（发现/加载成功/加载失败/状态切换）。参数是变化后的描述对象。</summary>
    public event EventHandler<PluginDescriptor>? DescriptorChanged;

    /// <summary>
    /// 一次卸载真正完成（含 ALC 回收验证结果）。热替换旧实例时也会触发。
    /// 与 <see cref="DescriptorChanged"/> 一样，可能在线程池线程上触发。
    /// </summary>
    public event EventHandler<PluginUnloadReport>? PluginUnloaded;

    /// <summary>
    /// <strong>ALC 卸载之前</strong>触发：订阅方必须在这里同步放掉所有指向该插件的托管资源，
    /// 尤其是 UI（把插件视图移出视觉树、断开绑定、清 DataContext）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么必须是"卸载前 + 同步"：卸载验证（WeakReference + 强制 GC）紧接着 teardown 执行，
    /// 只要还有一条 <c>视图对象 → 视图类型 → 程序集 → ALC</c> 的强引用链，回收就会失败。
    /// </para>
    /// <para>
    /// 事件在<strong>执行卸载的线程</strong>上触发（通常是线程池线程，卸载本身就在 UI 线程之外做）。
    /// UI 订阅方需要用 <c>Dispatcher.Invoke</c> 同步等待 UI 线程完成释放；
    /// 因此 UI 侧**不能**用 <c>.Result</c> / <c>.Wait()</c> 阻塞等待卸载，否则会死锁。
    /// </para>
    /// </remarks>
    public event EventHandler<PluginDescriptor>? PluginUnloading;

    public PluginLoader Loader => _loader;

    public string PluginRoot => _loader.PluginRoot;

    /// <summary>当前目录里的插件快照（含未加载/已失败项）。</summary>
    public IReadOnlyList<PluginDescriptor> Descriptors
    {
        get
        {
            lock (_sync)
            {
                return [.. _descriptors];
            }
        }
    }

    public bool IsLoaded(string pluginId)
    {
        lock (_sync)
        {
            return _handles.ContainsKey(pluginId);
        }
    }

    /// <summary>只发现、不加载。</summary>
    public IReadOnlyList<PluginSource> DiscoverSources() => _loader.Discover();

    /// <summary>把新发现的来源补进描述列表（状态 = Discovered），供 UI 预先展示"未加载"的插件。</summary>
    public int RefreshCatalog()
    {
        var added = 0;
        lock (_sync)
        {
            foreach (var source in _loader.Discover())
            {
                if (_descriptors.Any(d => string.Equals(d.SourcePath, source.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _descriptors.Add(new PluginDescriptor
                {
                    Id = source.Name,
                    DisplayName = source.Name,
                    SourcePath = source.Path,
                    SourceKind = source.Kind,
                    State = PluginState.Discovered,
                });
                added++;
            }
        }

        return added;
    }

    public Task<PluginDescriptor> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(PluginSource.Create(path), cancellationToken);

    public async Task<PluginDescriptor> LoadAsync(PluginSource source, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按发现顺序加载全部插件。单个插件失败不影响其它插件。</summary>
    public async Task<IReadOnlyList<PluginDescriptor>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var loaded = new List<PluginDescriptor>();

        foreach (var source in DiscoverSources())
        {
            if (_options.DisabledPlugins.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("插件 {Source} 在 DisabledPlugins 里，跳过。", source.Name);
                continue;
            }

            try
            {
                loaded.Add(await LoadAsync(source, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // 一个插件坏掉不能拖垮启动
                _logger.LogError(ex, "插件 {Source} 加载失败，已跳过。", source.Path);
            }
        }

        return loaded;
    }

    public async Task<PluginUnloadReport> UnloadAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UnloadCoreAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>卸载后按原来源重新加载（插件升级 / 开发内循环用）。</summary>
    public async Task<PluginDescriptor> ReloadAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PluginSource? source;
            lock (_sync)
            {
                source = _descriptors
                    .Where(d => string.Equals(d.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                    .Select(d => new PluginSource(d.SourcePath, d.SourceKind))
                    .FirstOrDefault();
            }

            if (source is null)
            {
                throw new PluginLoadException($"插件 {pluginId} 不在当前目录里，无法重载。");
            }

            await UnloadCoreAsync(pluginId, cancellationToken).ConfigureAwait(false);
            return await LoadCoreAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<string> GetToolNames(string pluginId)
    {
        lock (_sync)
        {
            return _handles.TryGetValue(pluginId, out var handle)
                ? handle.Descriptor.ToolNames
                : [];
        }
    }

    /// <summary>
    /// 列出某个插件提供的工具：合并「插件显式声明」（<see cref="IToolboxToolProvider"/>）
    /// 与「运行时反射发现」。
    /// </summary>
    /// <remarks>
    /// 返回的 <see cref="ToolboxToolInfo"/> 全是 BCL 类型（来自共享的契约程序集），
    /// 所以 UI 可以放心缓存 —— 它不会钉住插件 ALC。
    /// 展示信息以插件声明为准；<c>HasView</c> 以实际工具实例是否实现 <see cref="IToolboxToolView"/> 为准。
    /// </remarks>
    public IReadOnlyList<ToolboxToolInfo> GetToolInfos(string pluginId)
    {
        PluginHandle? handle;
        lock (_sync)
        {
            _handles.TryGetValue(pluginId, out handle);
        }

        if (handle is null)
        {
            return [];
        }

        // 1) 插件显式声明（展示信息优先）
        var declared = new Dictionary<string, ToolboxToolInfo>(StringComparer.OrdinalIgnoreCase);
        if (handle.Plugin is IToolboxToolProvider provider)
        {
            try
            {
                foreach (var info in provider.ProvideTools())
                {
                    if (!string.IsNullOrWhiteSpace(info.Id))
                    {
                        declared[info.Id] = info;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "插件 {PluginId} 的 ProvideTools() 抛出异常，退化为反射结果。", pluginId);
            }
        }

        // 2) 运行时发现的工具实例
        var merged = new Dictionary<string, ToolboxToolInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in handle.Tools)
        {
            var viewTool = tool as IToolboxToolView;
            var id = viewTool?.Id ?? tool.Name;
            declared.TryGetValue(id, out var info);

            merged[id] = new ToolboxToolInfo(
                Id: id,
                DisplayName: info?.DisplayName ?? viewTool?.DisplayName ?? tool.Name,
                Description: info?.Description ?? tool.Description,
                Category: info?.Category ?? viewTool?.Category,
                Order: info?.Order ?? viewTool?.Order ?? 0,
                HasView: viewTool is not null);
        }

        // 3) 声明了却没有对应实现的：也列出来（HasView=false），让 UI 能显示"工具缺失"
        foreach (var (id, info) in declared)
        {
            merged.TryAdd(id, info with { HasView = false });
        }

        return
        [
            .. merged.Values
                .OrderBy(i => i.Category ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(i => i.Order)
                .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// 为某个可视化工具创建视图。
    /// </summary>
    /// <returns>
    /// <see cref="ToolViewCreationResult.View"/> 是<strong>插件 ALC 里的对象</strong>：
    /// 调用方（UI 适配层）必须在卸载该插件之前把它移出视觉树并丢弃引用。
    /// </returns>
    public ToolViewCreationResult CreateToolView(string pluginId, string toolId)
    {
        PluginHandle? handle;
        lock (_sync)
        {
            _handles.TryGetValue(pluginId, out handle);
        }

        if (handle is null)
        {
            return ToolViewCreationResult.Fail($"插件 {pluginId} 未加载。");
        }

        var viewTool = handle.Tools
            .OfType<IToolboxToolView>()
            .FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));

        if (viewTool is null)
        {
            return ToolViewCreationResult.Fail($"插件 {pluginId} 没有名为 {toolId} 的可视化工具（或该插件未加载）。");
        }

        try
        {
            var view = viewTool.CreateView(handle.PluginContext);
            if (view is null)
            {
                return ToolViewCreationResult.Fail($"工具 {pluginId}/{toolId} 的 CreateView 返回了 null。");
            }

            _logger.LogInformation(
                "已创建插件视图 {PluginId}/{ToolId} → {ViewType}",
                pluginId,
                toolId,
                view.GetType().FullName);

            return ToolViewCreationResult.Ok(view);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建插件 {PluginId} 的工具 {ToolId} 视图失败。", pluginId, toolId);
            return ToolViewCreationResult.Fail($"创建视图失败：{ex.Message}");
        }
    }

    /// <summary>调用某个插件的工具。工具抛出的异常在这里转成 <see cref="ToolResult.Fail"/>，不向上冒泡。</summary>
    public async ValueTask<ToolResult> InvokeToolAsync(
        string pluginId,
        string toolName,
        IReadOnlyDictionary<string, string?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        PluginHandle? handle;
        lock (_sync)
        {
            _handles.TryGetValue(pluginId, out handle);
        }

        if (handle is null)
        {
            return ToolResult.Fail($"插件 {pluginId} 未加载。");
        }

        var tool = handle.FindTool(toolName);
        if (tool is null)
        {
            return ToolResult.Fail($"插件 {pluginId} 不提供名为 {toolName} 的工具。");
        }

        try
        {
            return await tool.ExecuteAsync(arguments ?? new Dictionary<string, string?>(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工具 {PluginId}/{Tool} 执行失败。", pluginId, toolName);
            return ToolResult.Fail($"工具 {toolName} 执行失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理不再使用的影子目录（zip 插件的解包结果）。
    /// 返回成功删掉的目录数；正被加载的目录会被跳过，删不掉的（Windows 文件锁）留到下次启动。
    /// </summary>
    public int CleanupShadowCopies()
    {
        var shadowRoot = _loader.ShadowRoot;
        if (!Directory.Exists(shadowRoot))
        {
            return 0;
        }

        HashSet<string> active;
        lock (_sync)
        {
            active = [.. _handles.Values.Select(h => h.Descriptor.ContentRoot)];
        }

        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(shadowRoot))
        {
            if (active.Any(contentRoot => IsSameOrUnder(directory, contentRoot)))
            {
                continue;
            }

            if (PluginPackageExtractor.TryDeleteDirectory(directory))
            {
                removed++;
            }
            else
            {
                _logger.LogInformation("影子目录仍被占用，留到下次启动清理：{Directory}", directory);
            }
        }

        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        string[] ids;
        lock (_sync)
        {
            ids = [.. _handles.Keys];
        }

        foreach (var id in ids)
        {
            try
            {
                await UnloadAsync(id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "退出时卸载插件 {PluginId} 失败。", id);
            }
        }

        _gate.Dispose();
    }

    private async Task<PluginDescriptor> LoadCoreAsync(PluginSource source, CancellationToken cancellationToken)
    {
        try
        {
            var handle = await _loader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            var descriptor = handle.Descriptor;

            PluginHandle? replaced;
            lock (_sync)
            {
                // 同 Id 的新实例顶替旧实例（等价于"热升级"）；旧实例稍后走完整卸载。
                _handles.Remove(descriptor.Id, out replaced);
                _handles[descriptor.Id] = handle;
                UpsertDescriptor(descriptor);
            }

            if (replaced is not null)
            {
                _logger.LogInformation("插件 {PluginId} 已存在加载实例，正在卸载被顶替的旧实例。", descriptor.Id);

                // 顶替旧实例同样要先让 UI 放手（旧实例的视图还在树上 → 回收必失败）
                RaiseUnloading(descriptor);
                await DetachHandleAsync(descriptor.Id, replaced, cancellationToken).ConfigureAwait(false);
                replaced = null;
            }

            RaiseChanged(descriptor);
            return descriptor;
        }
        catch (PluginLoadException ex)
        {
            var failed = ex.Descriptor ?? new PluginDescriptor
            {
                Id = source.Name,
                DisplayName = source.Name,
                SourcePath = source.Path,
                SourceKind = source.Kind,
            };

            failed.State = PluginState.Failed;
            failed.Error = ex.Message;

            lock (_sync)
            {
                UpsertDescriptor(failed);
            }

            RaiseChanged(failed);
            throw;
        }
    }

    private async Task<PluginUnloadReport> UnloadCoreAsync(string pluginId, CancellationToken cancellationToken)
    {
        PluginHandle? handle;
        PluginDescriptor? descriptor;
        lock (_sync)
        {
            _handles.Remove(pluginId, out handle);
            descriptor = _descriptors.FirstOrDefault(d => string.Equals(d.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        if (handle is null)
        {
            if (descriptor is not null)
            {
                descriptor.State = PluginState.Unloaded;
                RaiseChanged(descriptor);
            }

            return new PluginUnloadReport { PluginId = pluginId, WasLoaded = false, ContextCollected = true };
        }

        if (descriptor is not null)
        {
            descriptor.State = PluginState.Unloading;
            RaiseChanged(descriptor);

            // ⚠ 关键：在 ALC 卸载**之前**给订阅方（主要是 UI）一次同步放手的机会。
            //    视图还挂在视觉树上时，视图类型 → 程序集 → ALC 的强引用链会让回收验证必然失败。
            RaiseUnloading(descriptor);
        }

        var report = await DetachHandleAsync(pluginId, handle, cancellationToken).ConfigureAwait(false);

        // ⚠ 断开"本次调用"对句柄的最后一个强引用。句柄内部字段已在 ShutdownAndDetach 里逐个置空。
        handle = null;

        if (descriptor is not null)
        {
            descriptor.State = PluginState.Unloaded;
            descriptor.ToolNames = [];
            descriptor.Error = report.Error is not null
                ? $"卸载过程出错：{report.Error}"
                : report.ContextCollected
                    ? null
                    : $"ALC 未被回收：{report.AliveTargetType} 仍存活（{report.AliveTargetDetail}）";
            RaiseChanged(descriptor);
        }

        return report;
    }

    /// <summary>
    /// 真正执行卸载：同步 Teardown（跑在线程池）+ 可选回收验证。
    /// 调用方负责把持有所述句柄的局部变量置空。
    /// </summary>
    private async Task<PluginUnloadReport> DetachHandleAsync(
        string pluginId,
        PluginHandle handle,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // 用可空局部变量持有句柄：Teardown 之后立刻置空，确保本帧（含闭包）不再有强引用。
        PluginHandle? pending = handle;

        var (weakContext, probes, teardownError) = await Task.Run(
            () =>
            {
                Exception? error = null;
                var detached = PluginHandle.ShutdownAndDetach(pending!, cancellationToken, ex => error = ex);
                return (Weak: detached.Context, Probes: detached.Probes, Error: error);
            },
            CancellationToken.None).ConfigureAwait(false);

        pending = null;

        var verification = _options.VerifyUnload
            ? await Task.Run(
                () => UnloadVerifier.WaitForCollection(
                    weakContext,
                    probes,
                    _options.UnloadVerifyAttempts,
                    (attempt, alive) => _logger.LogDebug(
                        "卸载验证：插件 {PluginId} 第 {Attempt} 轮 GC 后仍存活 → {Alive}",
                        pluginId,
                        attempt,
                        alive)),
                CancellationToken.None).ConfigureAwait(false)
            : new UnloadVerification(true, 0, null, null, null);

        stopwatch.Stop();

        var report = new PluginUnloadReport
        {
            PluginId = pluginId,
            WasLoaded = true,
            ContextCollected = verification.Collected,
            CollectionAttempts = verification.Attempts,
            Elapsed = stopwatch.Elapsed,
            Error = teardownError?.Message,
            AliveTargetType = verification.AliveTargetType,
            AliveTargetDetail = verification.AliveTargetDetail,
            AlivePluginObjects = verification.AlivePluginObjects,
        };

        var collected = verification.Collected;
        var attempts = verification.Attempts;

        if (collected)
        {
            _logger.LogInformation(
                "插件 {PluginId} 已卸载并回收（{Attempts} 轮 GC，{Elapsed:F0} ms）。",
                pluginId,
                attempts,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        else
        {
            _logger.LogWarning(
                "插件 {PluginId} 已 Unload 但 ALC 未被回收（{Attempts} 轮 GC 后仍存活）：等于插件 DLL 仍被进程占用。"
                + "WeakReference.Target 实际类型={AliveTargetType}；{AliveTargetDetail}；仍存活的插件侧对象=[{AlivePluginObjects}]",
                pluginId,
                attempts,
                verification.AliveTargetType ?? "<null>",
                verification.AliveTargetDetail ?? "<无>",
                verification.AlivePluginObjects ?? "<无>");
        }

        RaiseUnloaded(report);
        return report;
    }

    private void UpsertDescriptor(PluginDescriptor descriptor)
    {
        var index = _descriptors.FindIndex(d => string.Equals(d.SourcePath, descriptor.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = _descriptors.FindIndex(d => string.Equals(d.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (index >= 0)
        {
            _descriptors[index] = descriptor;
        }
        else
        {
            _descriptors.Add(descriptor);
        }
    }

    private void RaiseChanged(PluginDescriptor descriptor)
    {
        try
        {
            DescriptorChanged?.Invoke(this, descriptor);
        }
        catch (Exception ex)
        {
            // 订阅者（UI）出错不能反过来影响插件生命周期
            _logger.LogWarning(ex, "DescriptorChanged 订阅者抛出异常，已忽略。");
        }
    }

    private void RaiseUnloaded(PluginUnloadReport report)
    {
        try
        {
            PluginUnloaded?.Invoke(this, report);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PluginUnloaded 订阅者抛出异常，已忽略。");
        }
    }

    private void RaiseUnloading(PluginDescriptor descriptor)
    {
        try
        {
            PluginUnloading?.Invoke(this, descriptor);
        }
        catch (Exception ex)
        {
            // 订阅方（UI）释放失败也要继续卸载：宁可回收失败并留下诊断，也不要卡死卸载流程。
            _logger.LogWarning(ex, "PluginUnloading 订阅者抛出异常，已忽略（可能影响 ALC 回收）。");
        }
    }

    private static bool IsSameOrUnder(string parent, string candidate) =>
        string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
