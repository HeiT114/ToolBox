using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Toolbox.Contracts;
using Toolbox.Views.Wpf;

namespace Toolbox.PluginHost.SmokeTest;

/// <summary>
/// 端到端冒烟测试：发现 → 目录加载 → zip 加载 → 私有依赖隔离 → 工具调用（真实探测）→
/// 配置覆盖 → 重载/幂等解包 → 失败清理 → 卸载回收 → 文件锁释放 → 跨 ALC 的插件 UI。
/// </summary>
/// <remarks>
/// <para>
/// 用控制台程序而不是 xunit，是为了让"卸载/回收"这类行为可以直接观察，
/// 也不给仓库引入测试框架依赖。断言失败时退出码为 1。
/// </para>
/// <para>
/// 测试对象是仓库里唯一的初始测试插件 <c>Toolbox.Plugin.SysInfo</c>（系统环境检测）：
/// 它同时带<strong>私有依赖</strong>（<c>Toolbox.Plugin.SysInfo.Core</c>）与<strong>带界面的工具</strong>，
/// 因此一个插件就覆盖了目录插件、zip 插件、ADR 隔离、配置覆盖、跨 ALC UI 全部路径。
/// </para>
/// <para>
/// 脚本把它 stage 成两种形态放进同一个插件根：目录形态 Id = <see cref="DirPluginId"/>，
/// zip 形态的清单里 Id 被改成 <see cref="ZipPluginId"/> ——
/// 因为 <see cref="PluginManager"/> 是"按 Id 找描述"的，两条来源共用同一个 Id 时
/// 描述状态会互相覆盖，断言就分不清谁被卸载了。取不同 Id 后两条来源各自独立可验证。
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>目录形态的插件 Id（来自 plugin.json）。</summary>
    private const string DirPluginId = "sysinfo";

    /// <summary>zip 形态的插件 Id（verify.ps1 在打包时改写清单得到）。</summary>
    private const string ZipPluginId = "sysinfo-zip";

    /// <summary>工具名（与插件 Id 无关，两种形态下都叫这个名字）。</summary>
    private const string ToolName = "sysinfo";

    /// <summary>
    /// STA + Dispatcher 泵：插件视图必须在 STA 线程上创建与访问，
    /// 所以给主线程装上 <see cref="DispatcherSynchronizationContext"/>，
    /// 让 <see cref="RunAsync"/> 里所有 await 续体都回到这根线程。
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

        var run = RunAsync(args);
        _ = run.ContinueWith(_ => dispatcher.InvokeShutdown(), TaskScheduler.Default);

        Dispatcher.Run();   // 泵到 RunAsync 结束
        return run.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        var pluginRoot = ResolvePluginRoot(args);
        var workRoot = Path.Combine(Path.GetTempPath(), "toolbox-smoketest", Guid.NewGuid().ToString("N"));

        Console.WriteLine("Toolbox 插件宿主 · 冒烟测试");
        Console.WriteLine($"  运行框架    : .NET {Environment.Version}");
        Console.WriteLine($"  插件目录    : {pluginRoot}");
        Console.WriteLine($"  临时工作目录: {workRoot}");

        if (!Directory.Exists(pluginRoot))
        {
            Console.WriteLine();
            Console.WriteLine($"插件目录不存在：{pluginRoot}");
            Console.WriteLine("请先运行 scripts/verify.ps1（它会把初始测试插件 stage 到 artifacts/plugins）。");
            return 2;
        }

        var settings = new TestSettingsProvider();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            builder.AddProvider(new ConsoleLoggerProvider(verbose));
        });
        services.AddSingleton<IPluginSettingsProvider>(settings);
        services.AddToolboxPluginHost(options =>
        {
            options.PluginDirectory = pluginRoot;
            options.ShadowDirectory = Path.Combine(workRoot, "shadow");
            options.DataDirectory = Path.Combine(workRoot, "data");
            options.VerifyUnload = true;
            options.UnloadVerifyAttempts = 10;
        });

        await using var root = services.BuildServiceProvider();
        var manager = root.GetRequiredService<PluginManager>();

        var unloadReports = new List<PluginUnloadReport>();
        manager.PluginUnloaded += (_, report) => unloadReports.Add(report);

        // ── 1. 发现 ────────────────────────────────────────────────────────────
        Check.Section("1. 发现来源（不加载任何程序集）");
        var sources = manager.DiscoverSources();
        foreach (var source in sources)
        {
            Console.WriteLine($"    - {source.Kind,-9} {source.Path}");
        }

        Check.Equal(2, sources.Count, "发现 2 个来源（同一插件的目录形态 + zip 形态）");
        var dirSource = sources.FirstOrDefault(s =>
            s.Kind == PluginSourceKind.Directory && s.Name.Contains("SysInfo", StringComparison.OrdinalIgnoreCase));
        var zipSource = sources.FirstOrDefault(s =>
            s.Kind == PluginSourceKind.Zip && s.Name.Contains("SysInfo", StringComparison.OrdinalIgnoreCase));
        Check.That(dirSource is not null, "发现目录插件（Toolbox.Plugin.SysInfo）");
        Check.That(zipSource is not null, "发现 zip 插件包（Toolbox.Plugin.SysInfo-1.0.0.zip）");

        Check.Equal(sources.Count, manager.RefreshCatalog(), $"RefreshCatalog 把 {sources.Count} 个来源补进描述列表");
        Check.That(manager.Descriptors.All(d => d.State == PluginState.Discovered), "此时都还是 Discovered 状态");

        if (dirSource is null || zipSource is null)
        {
            return Summarize();
        }

        // ── 2. 目录插件：加载 / 契约共享 / 私有依赖 / 真实工具调用 ─────────────
        Check.Section("2. 目录插件：加载 / 契约共享 / 私有依赖 / 真实工具调用");
        var directory = await manager.LoadAsync(dirSource.Path);
        Check.Equal(PluginState.Loaded, directory.State, "状态 = Loaded");
        Check.Equal(DirPluginId, directory.Id, "Id 来自 plugin.json");
        Check.Equal("1.0.0", directory.Version, "版本来自 plugin.json");
        Check.Equal(PluginSourceKind.Directory, directory.SourceKind, "来源形态 = Directory");
        Check.That(directory.ToolNames.SequenceEqual([ToolName]), $"工具列表 = [{ToolName}]");
        Check.That(
            !File.Exists(Path.Combine(dirSource.Path, "Toolbox.Contracts.dll")),
            "插件目录里没有 Toolbox.Contracts.dll（契约由宿主提供）");
        Check.That(
            directory.LoadedAssemblyNames.Any(n => n.Equals("Toolbox.Plugin.SysInfo", StringComparison.OrdinalIgnoreCase)),
            "插件 ALC 里加载了插件程序集本身");
        Check.That(
            !directory.LoadedAssemblyNames.Any(n => n.Equals("Toolbox.Contracts", StringComparison.OrdinalIgnoreCase)),
            "插件 ALC 里没有 Contracts 副本 → 走默认 ALC，类型同一性成立");
        Check.That(
            directory.LoadedAssemblyNames.Any(n => n.Equals("Toolbox.Plugin.SysInfo.Core", StringComparison.OrdinalIgnoreCase)),
            "私有依赖 Toolbox.Plugin.SysInfo.Core 加载在插件 ALC 内（目录形态同样走 ADR）");

        var probe = await manager.InvokeToolAsync(DirPluginId, ToolName, new Dictionary<string, string?> { ["filter"] = "机器" });
        Check.That(probe.Success, "sysinfo 工具执行成功（IToolboxTool 跨 ALC 可用，且构造注入成功）");
        Check.That(
            probe.Output is not null && probe.Output.Contains(Environment.MachineName, StringComparison.Ordinal),
            $"报告里确实带着本机机器名（独立对照 {Environment.MachineName}）");
        Check.That(
            probe.Output is not null && probe.Output.Contains("机器名", StringComparison.Ordinal),
            "filter=机器 命中了「机器名」条目");

        var fullReport = await manager.InvokeToolAsync(DirPluginId, ToolName);
        var reportLines = fullReport.Output?.Split('\n').Length ?? 0;
        Check.That(fullReport.Success, "不带参数也能采集成功");
        Check.That(reportLines >= 18, $"报告条目数合理（{reportLines} 行 = 标题 + 分隔线 + 条目）");
        Check.That(
            fullReport.Output is not null && fullReport.Output.Contains("系统环境检测", StringComparison.Ordinal),
            "未覆盖配置时使用 plugin.json 里的默认标题");
        Check.That(
            fullReport.Output is not null && fullReport.Output.Contains(Environment.SystemDirectory, StringComparison.Ordinal),
            "报告里带着系统目录（证明是真实探测，不是装饰文本）");

        // ── 3. zip 插件：影子解包 / 私有依赖隔离 ──────────────────────────────
        Check.Section("3. zip 插件：影子解包 / 私有依赖 / 配置");
        var dirUnload = await manager.UnloadAsync(DirPluginId);
        Check.That(dirUnload.ContextCollected, "切换形态前先干净卸载目录插件（ALC 已回收）");

        var zip = await manager.LoadAsync(zipSource.Path);
        Check.Equal(PluginState.Loaded, zip.State, "状态 = Loaded");
        Check.Equal(ZipPluginId, zip.Id, "Id 来自 zip 内改写过 id 的 plugin.json");
        Check.Equal("1.0.0", zip.Version, "版本来自 plugin.json");
        Check.Equal(PluginSourceKind.Zip, zip.SourceKind, "来源形态 = Zip");
        var zipContentRoot = zip.ContentRoot;
        Check.That(
            zipContentRoot.StartsWith(Path.Combine(workRoot, "shadow"), StringComparison.OrdinalIgnoreCase),
            "内容根落在影子解包目录，而不是 zip 原路径");
        Check.That(
            zip.LoadedAssemblyNames.Any(n => n.Equals("Toolbox.Plugin.SysInfo.Core", StringComparison.OrdinalIgnoreCase)),
            "私有依赖 Toolbox.Plugin.SysInfo.Core 加载在插件 ALC 内");
        Check.That(
            !AssemblyLoadContext.Default.Assemblies.Any(a =>
                string.Equals(a.GetName().Name, "Toolbox.Plugin.SysInfo.Core", StringComparison.OrdinalIgnoreCase)),
            "私有依赖没有泄漏到默认 ALC（AssemblyDependencyResolver 生效）");

        var cpuReport = await manager.InvokeToolAsync(ZipPluginId, ToolName, new Dictionary<string, string?> { ["filter"] = "逻辑处理器" });
        Check.That(cpuReport.Success, "zip 形态下工具执行成功");
        Check.That(
            cpuReport.Output is not null
                && cpuReport.Output.Contains(Environment.ProcessorCount.ToString(), StringComparison.Ordinal),
            $"报告里的逻辑处理器与本机一致（独立对照 {Environment.ProcessorCount}）");

        // ── 4. zip 幂等解包 + 主程序配置覆盖 + 重载 ───────────────────────────
        Check.Section("4. zip 幂等解包 + 主程序配置覆盖插件默认值 + 重载");
        var zipUnload = await manager.UnloadAsync(ZipPluginId);
        Check.That(zipUnload.ContextCollected, "卸载 zip 插件后 ALC 已回收");

        var zipAgain = await manager.LoadAsync(zipSource.Path);
        Check.Equal(zipContentRoot, zipAgain.ContentRoot, "同一 zip 再装一次复用同一个影子目录（按内容哈希幂等）");
        Check.That((await manager.UnloadAsync(ZipPluginId)).ContextCollected, "再次卸载同样完成回收");

        var dirAgain = await manager.LoadAsync(dirSource.Path);
        Check.Equal(PluginState.Loaded, dirAgain.State, "重新加载目录插件用于配置覆盖测试");

        settings.Set(DirPluginId, "title", "覆盖后的标题");
        var reloaded = await manager.ReloadAsync(DirPluginId);
        Check.Equal(PluginState.Loaded, reloaded.State, "重载后状态 = Loaded");

        var overridden = await manager.InvokeToolAsync(DirPluginId, ToolName);
        Check.That(
            overridden.Output is not null && overridden.Output.Contains("覆盖后的标题", StringComparison.Ordinal),
            "主程序配置覆盖了 plugin.json 的默认标题（重载后生效）");
        Check.Equal("覆盖后的标题", settings.GetSettings(DirPluginId)["title"], "覆盖配置确实来自主程序侧 provider");

        Check.That((await manager.UnloadAsync(DirPluginId)).ContextCollected, "配置覆盖测试后卸载并回收");

        // ── 5. 坏插件：失败要干净 ─────────────────────────────────────────────
        Check.Section("5. 坏插件：失败必须留下可读原因，且不泄漏 ALC");
        var brokenDirectory = Path.Combine(workRoot, "broken-plugin");
        Directory.CreateDirectory(brokenDirectory);
        File.WriteAllText(
            Path.Combine(brokenDirectory, "plugin.json"),
            """{ "id": "broken", "entryAssembly": "Missing.dll" }""");

        var threwPluginLoadException = false;
        try
        {
            await manager.LoadAsync(brokenDirectory);
        }
        catch (PluginLoadException ex)
        {
            threwPluginLoadException = true;
            Console.WriteLine($"    预期异常：{ex.Message}");
        }

        Check.That(threwPluginLoadException, "入口程序集缺失 → 抛 PluginLoadException");
        var brokenDescriptor = manager.Descriptors.FirstOrDefault(d =>
            string.Equals(d.SourcePath, brokenDirectory, StringComparison.OrdinalIgnoreCase));
        Check.That(
            brokenDescriptor is { State: PluginState.Failed, Error: not null },
            $"失败的插件进入描述列表且带原因：{brokenDescriptor?.Error}");

        // ── 6. 卸载与回收（无 UI）+ 文件锁释放 ────────────────────────────────
        Check.Section("6. 卸载：ALC 必须被真正回收，文件锁必须释放");
        var reloadForUnload = await manager.LoadAsync(dirSource.Path);
        Check.Equal(PluginState.Loaded, reloadForUnload.State, "为回收测试重新加载目录插件");

        var firstUnload = await manager.UnloadAsync(DirPluginId);
        Console.WriteLine($"    {firstUnload}");
        Check.That(firstUnload.WasLoaded, "插件确实处于已加载状态并被卸载");
        Check.That(firstUnload.ContextCollected, "ALC 已被 GC 回收（WeakReference 已失效）");

        var reloadAgain = await manager.LoadAsync(dirSource.Path);
        Check.Equal(PluginState.Loaded, reloadAgain.State, "卸载后同一路径可以再次加载（新 ALC）");
        Check.That((await manager.UnloadAsync(DirPluginId)).ContextCollected, "第二次卸载同样完成回收");

        var zipForLock = await manager.LoadAsync(zipSource.Path);
        var lockTarget = zipForLock.ContentRoot;
        Check.That((await manager.UnloadAsync(ZipPluginId)).ContextCollected, "zip 插件卸载后 ALC 已回收");

        // ALC 回收后，影子目录里的 dll 才可能被删除：这一步同时验证"文件锁已释放"
        var shadowDeleted = false;
        try
        {
            Directory.Delete(lockTarget, recursive: true);
            shadowDeleted = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    删除影子目录失败：{ex.GetType().Name}: {ex.Message}");
        }

        Check.That(shadowDeleted, "回收后影子目录可以删除（插件 dll 的文件锁已释放）");

        // ── 7. 跨 ALC 的插件 UI ───────────────────────────────────────────────
        Check.Section("7. UI 跨 ALC：工具枚举 / 视图创建 / 视图对卸载回收的影响");

        var uiPlugin = await manager.LoadAsync(dirSource.Path);
        Check.Equal(PluginState.Loaded, uiPlugin.State, "UI 插件已加载");
        Check.That(
            !uiPlugin.LoadedAssemblyNames.Any(n => n.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase)),
            "插件 ALC 里没有第二份 WPF（PresentationFramework 等由默认 ALC 提供）");

        var toolInfos = manager.GetToolInfos(DirPluginId);
        foreach (var info in toolInfos)
        {
            Console.WriteLine($"    - 工具 {info.Id}｜{info.DisplayName}｜分类={info.Category}｜顺序={info.Order}｜HasView={info.HasView}");
        }

        Check.Equal(1, toolInfos.Count, "声明 + 运行时发现合并后得到 1 个工具");
        Check.Equal("环境报告", toolInfos[0].DisplayName, "展示名来自插件声明（IToolboxToolProvider）");
        Check.Equal("诊断", toolInfos[0].Category, "分类来自插件声明");
        Check.Equal(10, toolInfos[0].Order, "排序来自插件声明");
        Check.That(toolInfos[0].HasView, "HasView=true（工具实现了 IToolboxToolView）");

        // 7.0 基线：从不创建视图，卸载必须干净回收（用来区分"视图钉住"与"加载路径本身钉住"）
        var baseline = await manager.UnloadAsync(DirPluginId);
        Console.WriteLine($"    [基线·无视图] {baseline}");
        Check.That(baseline.ContextCollected, "基线：从未创建视图时，UI 插件能干净回收");
        if (!baseline.ContextCollected)
        {
            Console.WriteLine($"    基线诊断：aliveTarget={baseline.AliveTargetType} / {baseline.AliveTargetDetail}");
        }

        var uiReloaded = await manager.LoadAsync(dirSource.Path);
        Check.Equal(PluginState.Loaded, uiReloaded.State, "重新加载 UI 插件用于视图测试");

        // 7.1 视图创建 + 跨 ALC 类型同一性（放在 NoInlining 的同步方法里，出栈即断开引用）
        VerifyViewCreationAcrossAlc(manager, DirPluginId, ToolName);

        // 7.2 视图还挂在视觉树上 → 回收必然失败（这就是"卸载前必须先摘视图"的实证）
        var pinnedReport = await UnloadWhileViewAttachedAsync(manager, DirPluginId, ToolName);
        Console.WriteLine($"    {pinnedReport}");
        Check.That(
            !pinnedReport.ContextCollected,
            "视图仍挂在视觉树上时 ALC 回收失败（预期：视图对象 → 视图类型 → 程序集 → ALC 的强引用链）");
        Check.That(
            pinnedReport.AliveTargetType is not null,
            $"回收失败时打印 WeakReference.Target 的实际类型：{pinnedReport.AliveTargetType}");
        Check.That(
            pinnedReport.AliveTargetDetail is not null
                && pinnedReport.AliveTargetDetail.Contains("Toolbox.Plugin.SysInfo", StringComparison.Ordinal),
            $"诊断详情里能看到仍然存活的插件程序集：{pinnedReport.AliveTargetDetail}");

        // 7.3 重新加载 → 走适配层 Show/Release → 卸载必须 1~2 轮回收
        var uiAgain = await manager.LoadAsync(dirSource.Path);
        Check.Equal(PluginState.Loaded, uiAgain.State, "为释放测试重新加载 UI 插件");

        var (releaseReport, releasedViewCollected) = await ShowReleaseAndUnloadAsync(manager, DirPluginId, ToolName);
        Console.WriteLine($"    {releaseReport}");
        Check.That(releaseReport.ContextCollected, "通过适配层摘除视图后，卸载成功回收 ALC");
        Check.That(
            releaseReport.CollectionAttempts <= 2,
            $"回收轮数 ≤ 2（实际 {releaseReport.CollectionAttempts} 轮）");
        Check.That(releasedViewCollected, "被释放的插件视图构建器对象本身也已被 GC 回收");

        // ── 8. 收尾 ───────────────────────────────────────────────────────────
        Check.Section("8. 收尾：影子目录清理与状态一致性");
        var removed = manager.CleanupShadowCopies();
        Console.WriteLine($"    清理影子目录 {removed} 个");
        Check.That(
            manager.Descriptors.All(d => d.State != PluginState.Loaded),
            "所有插件都不再处于 Loaded 状态");
        Check.That(
            !manager.IsLoaded(DirPluginId) && !manager.IsLoaded(ZipPluginId),
            "IsLoaded 全部为 false");

        // ── 9. 容器释放（主程序 OnExit 走的就是这条路）────────────────────────
        Check.Section("9. 容器释放：DisposeAsync 必须自动卸载残留插件");
        var finalLoad = await manager.LoadAsync(dirSource.Path);
        Check.Equal(PluginState.Loaded, finalLoad.State, "为释放测试重新加载插件");

        var reportsBeforeDispose = unloadReports.Count;
        await root.DisposeAsync();

        Check.That(!manager.IsLoaded(DirPluginId), "容器释放后插件不再被标记为已加载");
        Check.That(
            unloadReports.Count > reportsBeforeDispose,
            "释放过程通过 PluginUnloaded 事件上报了卸载结果");
        Check.That(
            unloadReports.Skip(reportsBeforeDispose).All(r => r.ContextCollected),
            "释放时的卸载同样完成了 ALC 回收（每一条报告 ContextCollected=true）");

        return Summarize();
    }

    /// <summary>
    /// 视图创建 + 跨 ALC 类型同一性。同步 + NoInlining：方法返回即断开对视图对象的引用。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyViewCreationAcrossAlc(PluginManager manager, string pluginId, string toolId)
    {
        var result = manager.CreateToolView(pluginId, toolId);
        Check.That(result.Success, $"CreateToolView 成功（{result.Error ?? "无错误"}）");
        Check.That(result.View is IPluginViewLifetime, "插件返回 IPluginViewLifetime 包装（宿主可在卸载前回调清理）");
        Check.That(
            (result.View as IPluginViewLifetime)?.View is FrameworkElement,
            "包装里的 View 是宿主侧 FrameworkElement → 两个 ALC 解析到同一份 WPF 程序集");
        Check.Equal(
            "Toolbox.Plugin.SysInfo.SysInfoView",
            result.View?.GetType().FullName,
            "视图构建器类型确实来自插件程序集");
        Check.That(
            result.View is not null && !typeof(DependencyObject).IsAssignableFrom(result.View.GetType()),
            "视图构建器不是 DependencyObject 派生类型（否则 WPF 类型缓存会永久钉住插件 ALC）");
        // 出栈即无引用，后续卸载不会因为这里残留的强引用而失败
    }

    /// <summary>
    /// 故意让视图留在视觉树上再卸载：回收必须失败，并且要能打印出"谁还活着"。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<PluginUnloadReport> UnloadWhileViewAttachedAsync(
        PluginManager manager,
        string pluginId,
        string toolId)
    {
        var host = new PluginToolViewHost();
        if (!host.Show(manager, pluginId, toolId, out var error))
        {
            throw new InvalidOperationException($"挂载插件视图失败：{error}");
        }

        host.Measure(new Size(800, 600));
        host.Arrange(new Rect(0, 0, 800, 600));
        host.UpdateLayout();

        Check.That(host.Content is FrameworkElement, "视图已挂到 ContentControl 上");
        Console.WriteLine($"    视图逻辑父级 = {(host.Content as FrameworkElement)?.Parent?.GetType().Name ?? "<null>"}");

        // ⚠ 故意不 Release：视图仍挂在视觉树上
        var report = await manager.UnloadAsync(pluginId);
        GC.KeepAlive(host);

        host.Release();   // 收尾，避免影响后续断言
        GC.KeepAlive(host);
        return report;
    }

    /// <summary>
    /// 正规路径：适配层 Show → 布局 → Release（摘视图）→ 卸载，并检查视图对象本身是否被回收。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(PluginUnloadReport Report, bool ReleasedViewCollected)> ShowReleaseAndUnloadAsync(
        PluginManager manager,
        string pluginId,
        string toolId)
    {
        PluginToolViewHost? host = new();
        try
        {
            if (!host.Show(manager, pluginId, toolId, out var error))
            {
                throw new InvalidOperationException($"挂载插件视图失败：{error}");
            }

            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            host.UpdateLayout();
            Check.That(host.Content is FrameworkElement, "（正规路径）视图已挂载并完成一次布局");

            host.Release();

            var viewReference = host.ReleasedViewReference;
            var pluginObjectReference = host.ReleasedPluginObjectReference;
            Check.That(host.Content is null, "Release 后 Content 已置空（视图移出视觉树/逻辑树）");
            Check.That(viewReference is not null, "适配层保留了被释放视图元素的弱引用（诊断用）");
            Check.That(pluginObjectReference is not null, "适配层保留了被释放插件对象的弱引用（诊断用）");

            host = null;   // 断开适配层强引用（它内部只剩弱引用，仍保持纪律）

            var report = await manager.UnloadAsync(pluginId);

            // 卸载验证里已经跑过强制 GC，这里直接看插件对象是否也死了
            var collected = pluginObjectReference is not null && !pluginObjectReference.IsAlive;
            if (!collected)
            {
                Console.WriteLine(
                    $"    插件对象仍存活：{pluginObjectReference?.Target?.GetType().FullName ?? "<target 为 null>"}");
            }

            return (report, collected);
        }
        finally
        {
            host?.Release();
        }
    }

    private static int Summarize()
    {
        Console.WriteLine();
        Console.WriteLine($"合计 {Check.Total} 项断言，失败 {Check.Failures} 项。");
        Console.WriteLine(Check.Failures == 0 ? "结果：全部通过 ✅" : "结果：存在失败 ❌");
        return Check.Failures == 0 ? 0 : 1;
    }

    private static string ResolvePluginRoot(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--plugins", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        // 默认：向上找到仓库根（有 Toolbox.slnx），取 artifacts/plugins
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Toolbox.slnx")))
            {
                return Path.Combine(directory.FullName, "artifacts", "plugins");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "plugins");
    }
}
