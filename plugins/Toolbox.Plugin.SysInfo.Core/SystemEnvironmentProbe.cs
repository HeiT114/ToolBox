using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Toolbox.Plugin.SysInfo.Core;

/// <summary>一条环境探测结果。</summary>
/// <param name="Key">稳定键（脚本/程序过滤用，英文）。</param>
/// <param name="Label">给人看的中文标签。</param>
/// <param name="Value">采集到的值。</param>
public sealed record EnvironmentFact(string Key, string Label, string Value);

/// <summary>一次系统环境快照。</summary>
/// <param name="CapturedAt">采集时刻（本地时间，秒精度）。</param>
/// <param name="Facts">采集到的全部条目。</param>
public sealed record EnvironmentSnapshot(string CapturedAt, IReadOnlyList<EnvironmentFact> Facts)
{
    /// <summary>
    /// 渲染成可读报告。
    /// </summary>
    /// <param name="title">报告标题（来自插件配置，缺省用内置标题）。</param>
    /// <param name="filter">
    /// 过滤关键字：命中 <see cref="EnvironmentFact.Key"/> / <see cref="EnvironmentFact.Label"/> /
    /// <see cref="EnvironmentFact.Value"/> 任意一处即保留；为空则输出全部。
    /// </param>
    public string ToReport(string? title, string? filter)
    {
        var caption = string.IsNullOrWhiteSpace(title) ? "系统环境检测" : title.Trim();

        var selected = string.IsNullOrWhiteSpace(filter)
            ? Facts
            : [.. Facts.Where(fact =>
                fact.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || fact.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || fact.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        var builder = new StringBuilder();
        builder.AppendLine($"{caption} · {selected.Count} / {Facts.Count} 项 · {CapturedAt}");
        builder.AppendLine(new string('-', 52));

        if (selected.Count == 0)
        {
            builder.AppendLine($"（没有匹配「{filter}」的条目）");
            return builder.ToString().TrimEnd();
        }

        foreach (var fact in selected)
        {
            builder.AppendLine($"{fact.Label,-10} = {fact.Value}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>一行摘要（插件初始化日志用）。</summary>
    public string Summarize()
    {
        var machine = Find("machine") ?? "?";
        var os = Find("os") ?? "?";
        return $"{Facts.Count} 项 · 机器 {machine} · {os}";
    }

    private string? Find(string key) =>
        Facts.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>
/// 环境探测器：只用 BCL，不依赖 WPF，也不依赖宿主。
/// </summary>
/// <remarks>
/// 它是插件的<strong>私有依赖</strong>：宿主进程里不会加载这个程序集，
/// 它只活在插件自己的 ALC 里（zip 插件由 AssemblyDependencyResolver 从插件 deps.json 解析）。
/// </remarks>
public static class SystemEnvironmentProbe
{
    /// <summary>采集当前系统环境。</summary>
    public static EnvironmentSnapshot Capture()
    {
        var gc = GC.GetGCMemoryInfo();

        var facts = new List<EnvironmentFact>
        {
            new("machine", "机器名", Safe(() => Environment.MachineName)),
            new("user", "当前用户", Safe(() => $@"{Environment.UserDomainName}\{Environment.UserName}")),
            new("os", "操作系统", Safe(() => RuntimeInformation.OSDescription)),
            new("osVersion", "OS 版本", Safe(() => Environment.OSVersion.Version.ToString())),
            new("osArch", "系统架构", Safe(() => RuntimeInformation.OSArchitecture.ToString())),
            new("processArch", "进程架构", Safe(() => RuntimeInformation.ProcessArchitecture.ToString())),
            new("is64", "64 位进程", Safe(() => Environment.Is64BitProcess ? "是" : "否")),
            new("runtime", "运行时", Safe(() => RuntimeInformation.FrameworkDescription)),
            new("clrVersion", "CLR 版本", Safe(() => Environment.Version.ToString())),
            new("cpuCount", "逻辑处理器", Safe(() => Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture))),
            new("cpu", "处理器标识", Safe(() => Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "（系统未提供）")),
            new("memory", "可用内存", Safe(() => $"{gc.TotalAvailableMemoryBytes / 1024d / 1024d / 1024d:F1} GB")),
            new("pageSize", "系统页大小", Safe(() => $"{Environment.SystemPageSize} 字节")),
            new("systemDir", "系统目录", Safe(() => Environment.SystemDirectory)),
            new("currentDir", "当前目录", Safe(() => Environment.CurrentDirectory)),
            new("timezone", "时区", Safe(() => TimeZoneInfo.Local.DisplayName)),
            new("culture", "区域", Safe(() => CultureInfo.CurrentCulture.Name)),
            new("workingSet", "进程工作集", Safe(() => $"{Environment.WorkingSet / 1024d / 1024d:F1} MB")),
            new("uptime", "进程已运行", Safe(() => TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"hh\:mm\:ss"))),
        };

        return new EnvironmentSnapshot(
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            facts);
    }

    /// <summary>
    /// 单项探测失败不能拖垮整份报告（例如某些精简系统上没有 PROCESSOR_IDENTIFIER）。
    /// </summary>
    private static string Safe(Func<string> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            return $"（探测失败：{ex.GetType().Name}）";
        }
    }
}
