using System.IO;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
// WinForms/Win32 的 System.Drawing 被隐式 using：显式取 WPF 的颜色类型。
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Toolbox.App.Services;

/// <summary>应用主题。</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// 主题服务：把整套调色板（<c>*Brush</c> 键）写进 <see cref="System.Windows.Application.Current"/>
/// 的资源字典，界面里所有 <c>DynamicResource</c> 引用会立即换色。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是"整体换实例"而不是"改同一个刷子的颜色"：WPF 在把 Freezable 塞进
/// Style/模板后会把它冻结（read-only），共享刷子根本改不动；而 DynamicResource
/// 挂的是"字典里的键"，换掉键对应的实例即可无缝刷新，且与冻结与否无关。
/// </para>
/// <para>
/// 刻意不给插件视图做任何主题化：插件视图自带颜色，主题只管宿主自己的外观。
/// </para>
/// </remarks>
public sealed class ThemeService
{
    private static readonly Dictionary<string, string> LightPalette = new(StringComparer.Ordinal)
    {
        ["AppBackgroundBrush"] = "#F3F5F9",
        ["SurfaceBrush"] = "#FFFFFF",
        ["SurfaceMutedBrush"] = "#F7F9FC",
        ["SurfaceHoverBrush"] = "#EDF0F6",
        ["BorderSoftBrush"] = "#E4E9F1",
        ["BorderStrongBrush"] = "#D0D7E4",
        ["TextPrimaryBrush"] = "#1B2333",
        ["TextSecondaryBrush"] = "#5B6478",
        ["TextMutedBrush"] = "#8892A6",
        ["AccentBrush"] = "#4F46E5",
        ["AccentHoverBrush"] = "#4338CA",
        ["AccentPressedBrush"] = "#3730A3",
        ["AccentSoftBrush"] = "#E9EBFE",
        ["AccentSofterBrush"] = "#F3F4FF",
        ["SuccessBrush"] = "#15803D",
        ["SuccessSoftBrush"] = "#E6F6EC",
        ["DangerBrush"] = "#B91C1C",
        ["DangerSoftBrush"] = "#FDECEC",
        ["WarningBrush"] = "#B45309",
        ["WarningSoftBrush"] = "#FEF3E2",
        ["NeutralSoftBrush"] = "#EEF1F6",
    };

    private static readonly Dictionary<string, string> DarkPalette = new(StringComparer.Ordinal)
    {
        ["AppBackgroundBrush"] = "#0E131B",
        ["SurfaceBrush"] = "#161C26",
        ["SurfaceMutedBrush"] = "#1B2230",
        ["SurfaceHoverBrush"] = "#262F3E",
        ["BorderSoftBrush"] = "#2E3849",
        ["BorderStrongBrush"] = "#3E4A5E",
        ["TextPrimaryBrush"] = "#E8ECF5",
        ["TextSecondaryBrush"] = "#A8B4CA",
        ["TextMutedBrush"] = "#8A96AC",
        ["AccentBrush"] = "#8B84F6",
        ["AccentHoverBrush"] = "#9A94F8",
        ["AccentPressedBrush"] = "#6A63E0",
        ["AccentSoftBrush"] = "#2B3358",
        ["AccentSofterBrush"] = "#252D4C",
        ["SuccessBrush"] = "#35D08A",
        ["SuccessSoftBrush"] = "#123125",
        ["DangerBrush"] = "#FF8078",
        ["DangerSoftBrush"] = "#351A1C",
        ["WarningBrush"] = "#F0B445",
        ["WarningSoftBrush"] = "#332812",
        ["NeutralSoftBrush"] = "#262F3E",
    };

    private readonly ILogger<ThemeService> _logger;
    private readonly string _preferenceFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Toolbox",
        "theme.txt");

    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>当前主题。</summary>
    public AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>主题变化（设置窗口/状态栏提示用）。</summary>
    public event Action<AppTheme>? ThemeChanged;

    /// <summary>
    /// 启动时初始化：<c>--theme light|dark</c> 参数优先，其次上次保存的选择，默认浅色。
    /// 必须在主窗口创建<strong>之前</strong>调用（窗口的 DynamicResource 首次解析发生在 Show 时）。
    /// </summary>
    public void Initialize(string[] args)
    {
        var theme = AppTheme.Light;

        var fromArg = args.FirstOrDefault(a =>
            a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase));
        if (fromArg is not null)
        {
            theme = Parse(fromArg.Substring("--theme=".Length)) ?? theme;
        }
        else
        {
            try
            {
                if (File.Exists(_preferenceFile))
                {
                    theme = Parse(File.ReadAllText(_preferenceFile).Trim()) ?? theme;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取主题偏好失败，使用默认浅色。");
            }
        }

        Apply(theme, persist: false);
    }

    /// <summary>切换到指定主题（立即生效并记住）。</summary>
    public void Apply(AppTheme theme, bool persist = true)
    {
        var palette = theme == AppTheme.Dark ? DarkPalette : LightPalette;
        var resources = System.Windows.Application.Current.Resources;

        foreach (var (key, hex) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            resources[key] = brush;
        }

        Current = theme;

        if (persist)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_preferenceFile)!);
                File.WriteAllText(_preferenceFile, theme == AppTheme.Dark ? "dark" : "light");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存主题偏好失败（不影响本次使用）。");
            }
        }

        ThemeChanged?.Invoke(theme);
    }

    /// <summary>浅色 ⇄ 深色。</summary>
    public void Toggle() => Apply(Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);

    private static AppTheme? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dark" or "深色" => AppTheme.Dark,
        "light" or "浅色" => AppTheme.Light,
        _ => null,
    };
}
