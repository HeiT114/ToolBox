using System.Runtime.Loader;

namespace Toolbox.PluginHost;

/// <summary>一次卸载操作的结果。</summary>
public sealed record PluginUnloadReport
{
    public required string PluginId { get; init; }

    /// <summary>是否真的卸载了一个已加载的插件（false = 本来就没加载）。</summary>
    public bool WasLoaded { get; init; }

    /// <summary>
    /// ALC 是否已被 GC 真正回收。<see langword="false"/> 通常意味着还有引用钉住了插件
    /// （宿主缓存了插件对象、插件启动了后台线程、UI 视图还在视觉树上……），
    /// 也可能只是"还没轮到 GC"——本报告已经做过强制 GC 循环。
    /// </summary>
    public bool ContextCollected { get; init; }

    /// <summary>强制 GC 尝试了几轮才判定。</summary>
    public int CollectionAttempts { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>卸载过程中的异常（Shutdown 抛错也会记在这里，卸载流程继续走完）。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 回收失败时 <c>WeakReference.Target</c> 的**实际类型全名**（辅助定位是谁钉住了 ALC）。
    /// 正常情况下这里应该是 <c>Toolbox.PluginHost.PluginLoadContext</c>。
    /// </summary>
    public string? AliveTargetType { get; init; }

    /// <summary>回收失败时 ALC 的详细快照：ALC 名称 + 它仍然装着的程序集（定位"到底哪份代码还活着"）。</summary>
    public string? AliveTargetDetail { get; init; }

    /// <summary>
    /// 回收失败时仍然存活的<strong>插件侧对象</strong>清单（插件实例 / 工具 / DI 容器 / 上下文 / 入口程序集）。
    /// 全为空说明钉住 ALC 的不是这些对象，而是别的宿主级缓存持有了插件类型。
    /// </summary>
    public string? AlivePluginObjects { get; init; }

    /// <summary>卸载流程本身是否无异常。注意：这不代表 ALC 已回收，回收看 <see cref="ContextCollected"/>。</summary>
    public bool Success => Error is null;

    public override string ToString() =>
        $"{PluginId}: unloaded={WasLoaded}, collected={ContextCollected} (attempts={CollectionAttempts}), {Elapsed.TotalMilliseconds:F0} ms"
        + (Error is null ? string.Empty : $", error={Error}")
        + (ContextCollected ? string.Empty : $", aliveTarget={AliveTargetType}")
        + (AlivePluginObjects is null ? string.Empty : $", aliveObjects=[{AlivePluginObjects}]");
}

/// <summary>插件侧对象的存活探针（弱引用 + 可读名字）。</summary>
/// <param name="Name">探针名字（类型全名等）。</param>
/// <param name="Reference">弱引用；<c>IsAlive</c> 为真表示该对象仍被某事钉住。</param>
internal readonly record struct PluginLivenessProbe(string Name, WeakReference Reference);

/// <summary>回收验证的返回值。</summary>
/// <param name="Collected">ALC 是否已可回收。</param>
/// <param name="Attempts">用掉了几轮强制 GC。</param>
/// <param name="AliveTargetType">仍存活时 <c>WeakReference.Target</c> 的实际类型全名。</param>
/// <param name="AliveTargetDetail">仍存活时 ALC 的名称与程序集快照。</param>
/// <param name="AlivePluginObjects">仍存活的插件侧对象清单。</param>
internal readonly record struct UnloadVerification(
    bool Collected,
    int Attempts,
    string? AliveTargetType,
    string? AliveTargetDetail,
    string? AlivePluginObjects);

/// <summary>卸载后的回收验证：WeakReference + 强制 GC 循环。</summary>
internal static class UnloadVerifier
{
    /// <summary>
    /// 判定 <paramref name="contextReference"/> 指向的 ALC 是否已可回收。
    /// </summary>
    /// <remarks>
    /// 必须<strong>同步</strong>执行且不跨 await：任何 async 状态机都可能把插件对象留在堆上，
    /// 让"验证"本身成为钉住 ALC 的元凶。
    /// </remarks>
    /// <param name="probes">插件侧对象探针；每轮把"仍存活"的名字回调出去，便于定位凶手。</param>
    /// <param name="onAttempt">每轮回调（轮次、仍存活的探针名）。</param>
    public static UnloadVerification WaitForCollection(
        WeakReference contextReference,
        IReadOnlyList<PluginLivenessProbe> probes,
        int maxAttempts,
        Action<int, string>? onAttempt = null)
    {
        ArgumentNullException.ThrowIfNull(contextReference);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ForceFullCollectionCycle();

            if (!contextReference.IsAlive)
            {
                return new UnloadVerification(true, attempt, null, null, null);
            }

            if (onAttempt is not null)
            {
                var alive = probes.Where(p => p.Reference.IsAlive).Select(p => p.Name).ToArray();
                onAttempt(attempt, alive.Length == 0 ? "<插件侧对象已全部回收，仍有宿主级缓存持有插件类型>" : string.Join("；", alive));
            }

            // 给终结器队列/线程池上的残留任务一点时间；这只是重试，不是在等它自己消失。
            Thread.Sleep(15 * attempt);
        }

        var (targetType, detail) = DescribeAlive(contextReference);
        var aliveObjects = probes.Where(p => p.Reference.IsAlive).Select(p => p.Name).ToArray();

        return new UnloadVerification(
            false,
            maxAttempts,
            targetType,
            detail,
            aliveObjects.Length == 0 ? "<无：插件侧对象都已回收，是宿主级缓存持有插件类型>" : string.Join("；", aliveObjects));
    }

    /// <summary>
    /// 一轮"完整回收周期"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// CLR 对 collectible ALC 的回收是<strong>分阶段</strong>的：GC 先判定 ALC 不可达并清空它的程序集列表，
    /// 承载 ALC 的托管包装对象、以及被 WPF 路径触碰过的 loader allocator 结构（类型句柄、委托的运行时方法）
    /// 往往要再经过一到两次收集才真正释放。因此"一轮"这里定义为
    /// <c>收集 → 终结器 → 收集 → 终结器 → 收集</c>，
    /// 这样"回收用了几轮"这个度量在"有 UI"与"无 UI"场景下都稳定可比。
    /// </para>
    /// <para>
    /// 注意这不改变判定标准：真正被钉住的 ALC（例如视图还挂在视觉树上）无论推多少轮都不会回收。
    /// </para>
    /// </remarks>
    private static void ForceFullCollectionCycle()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// 回收失败时的现场取证：<c>WeakReference.Target</c> 的实际类型 + ALC 里还装着的程序集。
    /// </summary>
    private static (string? TargetType, string? Detail) DescribeAlive(WeakReference contextReference)
    {
        try
        {
            // 读取 Target 只是"看一眼"，不会影响 ALC 的回收（WeakReference 不持有强引用）。
            var target = contextReference.Target;
            if (target is null)
            {
                return (null, null);
            }

            var targetType = target.GetType().FullName ?? target.GetType().Name;

            if (target is AssemblyLoadContext context)
            {
                string assemblies;
                try
                {
                    assemblies = string.Join(", ", context.Assemblies.Select(a => a.GetName().Name));
                }
                catch (Exception ex)
                {
                    assemblies = $"<枚举失败: {ex.GetType().Name}>";
                }

                return (targetType, $"ALC 名称='{context.Name}'，仍装载 [{assemblies}]，IsCollectible={context.IsCollectible}");
            }

            return (targetType, null);
        }
        catch (Exception ex)
        {
            return ($"<取证失败: {ex.GetType().Name}>", null);
        }
    }
}
