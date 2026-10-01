using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Toolbox.App.Services;
using Toolbox.PluginHost;

namespace Toolbox.App.ViewModels;

/// <summary>设置窗口里"某个插件被主程序覆盖的配置项"一行。</summary>
/// <param name="PluginId">插件 Id。</param>
/// <param name="SettingsText">该插件被覆盖的键值（多行）。</param>
public sealed record SettingsOverrideRow(string PluginId, string SettingsText);

/// <summary>设置左侧分类导航的一行（分类放左、内容放右）。</summary>
public sealed record SettingsSectionRow(string Title, string Subtitle);

/// <summary>
/// 设置窗口的视图模型：<strong>只读</strong>展示"当前真正生效"的宿主配置。
/// </summary>
/// <remarks>
/// <para>
/// 刻意<strong>不做写回</strong>：<see cref="PluginHostOptions"/> 在宿主构造时就已经被
/// <c>PluginLoader</c> / <c>PluginManager</c> 捕获，运行时修改不会生效 —— 做成"可编辑"的表单
/// 只会误导用户。设置窗口因此只负责"让你看清现在生效的是什么、文件在哪"，
/// 并提供打开对应目录的快捷入口。
/// </para>
/// <para>
/// 这里只读字符串与路径，不引用任何插件对象，也不改变任何契约/协议。
/// </para>
/// </remarks>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly PluginHostOptions _options;
    private readonly ILogger _logger;
    private readonly ThemeService _theme;
    private readonly UiPreferences _ui;
    private readonly PluginManager _manager;
    private readonly Action _onCatalogChanged;
    private string _status = "只读：修改配置请编辑 appsettings.json 后重启。";
    private int _selectedSection;

    public SettingsViewModel(
        PluginHostOptions options,
        PluginManager manager,
        IConfiguration configuration,
        ILogger logger,
        ThemeService theme,
        UiPreferences ui,
        Action onCatalogChanged)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _onCatalogChanged = onCatalogChanged ?? throw new ArgumentNullException(nameof(onCatalogChanged));
        _theme.ThemeChanged += _ =>
        {
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            Status = _theme.Current == AppTheme.Dark ? "已切换到深色主题（立即生效）" : "已切换到浅色主题（立即生效）";
        };
        _ui.FontScaleChanged += _ =>
        {
            OnPropertyChanged(nameof(Scale100));
            OnPropertyChanged(nameof(Scale125));
            OnPropertyChanged(nameof(Scale150));
        };
        _ui.CloseToTrayChanged += _ => OnPropertyChanged(nameof(CloseToTray));
        _ui.GetLaunchAtStartup();

        Sections =
        [
            new("外观", "主题（深色内测）· 字号缩放"),
            new("目录", "插件/影子/数据/日志路径 · 重新扫描与重载"),
            new("插件宿主", "共享程序集 · 卸载验证 · 运行时"),
            new("启动与托盘", "开机自启 · 关窗最小化到托盘"),
            new("配置覆盖", "PluginHost:Plugins:<id> 的覆盖值"),
            new("配置文件", "appsettings.json 路径与说明"),
        ];

        // PluginManager.PluginRoot 已经是解析后的绝对路径
        PluginDirectory = manager.PluginRoot;
        ShadowDirectory = options.ResolvePath(options.ShadowDirectory);
        DataDirectory = options.ResolvePath(options.DataDirectory);
        LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        AppBaseDirectory = AppContext.BaseDirectory;
        RuntimeLine = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}";

        SharedAssembliesText = options.SharedAssemblies.Length == 0
            ? "（无）"
            : string.Join(", ", options.SharedAssemblies);

        DisabledPluginsText = options.DisabledPlugins.Length == 0
            ? "（无）"
            : string.Join(", ", options.DisabledPlugins);

        VerifyUnloadText = options.VerifyUnload
            ? $"开启（最多 {options.UnloadVerifyAttempts} 轮强制 GC）"
            : "关闭（不验证 ALC 回收）";

        LogLevelText = configuration["Logging:LogLevel:Default"] ?? "Information";

        PluginOverrides =
        [
            .. configuration.GetSection("PluginHost:Plugins").GetChildren()
                .Select(section => new SettingsOverrideRow(
                    section.Key,
                    string.Join(
                        Environment.NewLine,
                        section.GetChildren().Select(child => $"{child.Key} = {child.Value}")))),
        ];

        OpenPluginDirectoryCommand = new AsyncRelayCommand(() => OpenFolderAsync(PluginDirectory));
        OpenDataDirectoryCommand = new AsyncRelayCommand(() => OpenFolderAsync(DataDirectory));
        OpenLogDirectoryCommand = new AsyncRelayCommand(() => OpenFolderAsync(LogDirectory));
        OpenConfigDirectoryCommand = new AsyncRelayCommand(() => OpenFolderAsync(AppBaseDirectory));

        // 1.0.0-beta：「重新扫描 / 重新加载」从左侧工具栏移进设置窗口
        RescanCommand = new AsyncRelayCommand(() =>
        {
            try
            {
                _manager.RefreshCatalog();
                _onCatalogChanged();
                Status = $"插件目录已重新扫描：发现 {_manager.Descriptors.Count()} 个插件";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "重新扫描插件目录失败。");
                Status = $"重新扫描失败：{ex.Message}";
            }

            return Task.CompletedTask;
        });

        ReloadCommand = new AsyncRelayCommand(async () =>
        {
            try
            {
                Status = "正在重新加载全部插件…";
                var loaded = await _manager.LoadAllAsync();
                _onCatalogChanged();
                Status = $"重新加载完成：成功 {loaded.Count} 个";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新加载全部插件失败。");
                Status = $"重新加载失败：{ex.Message}";
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── 路径 ──────────────────────────────────────────────────────────────

    public string PluginDirectory { get; }

    public string ShadowDirectory { get; }

    public string DataDirectory { get; }

    public string LogDirectory { get; }

    public string ConfigFilePath { get; }

    public string AppBaseDirectory { get; }

    // ── 宿主选项 ──────────────────────────────────────────────────────────

    public string SharedAssembliesText { get; }

    public string DisabledPluginsText { get; }

    public string VerifyUnloadText { get; }

    public string LogLevelText { get; }

    public string RuntimeLine { get; }

    /// <summary>主程序对插件配置的覆盖（<c>PluginHost:Plugins:&lt;id&gt;</c>）。</summary>
    public IReadOnlyList<SettingsOverrideRow> PluginOverrides { get; }

    /// <summary>版本号（InformationalVersion，如 1.0.0-beta）。</summary>
    public string VersionText
    {
        get
        {
            var informational = typeof(SettingsViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
            {
                return "v" + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            }

            var plus = informational.IndexOf('+');
            return "v" + (plus > 0 ? informational[..plus] : informational);
        }
    }

    // ── 外观（主题）────────────────────────────────────────────────────────

    /// <summary>浅色主题选中态（设置窗口胶囊）。</summary>
    public bool IsLightTheme
    {
        get => _theme.Current == AppTheme.Light;
        set
        {
            if (value)
            {
                _theme.Apply(AppTheme.Light);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>深色主题选中态（设置窗口胶囊）。</summary>
    public bool IsDarkTheme
    {
        get => _theme.Current == AppTheme.Dark;
        set
        {
            if (value)
            {
                _theme.Apply(AppTheme.Dark);
                OnPropertyChanged();
            }
        }
    }

    // ── 分类导航（分类放左、内容放右）───────────────────────────────────────

    public ObservableCollection<SettingsSectionRow> Sections { get; }

    /// <summary>当前选中的分类下标（0 外观 … 5 配置文件，右侧内容据此切换）。</summary>
    public int SelectedSection
    {
        get => _selectedSection;
        set => SetField(ref _selectedSection, value);
    }

    // ── 字号缩放 ──────────────────────────────────────────────────────────

    public bool Scale100
    {
        get => Math.Abs(_ui.FontScale - 1.0) < 0.001;
        set { if (value) { _ui.SetFontScale(1.0); OnPropertyChanged(); RaiseScaleStates(); } }
    }

    public bool Scale125
    {
        get => Math.Abs(_ui.FontScale - 1.25) < 0.001;
        set { if (value) { _ui.SetFontScale(1.25); OnPropertyChanged(); RaiseScaleStates(); } }
    }

    public bool Scale150
    {
        get => Math.Abs(_ui.FontScale - 1.5) < 0.001;
        set { if (value) { _ui.SetFontScale(1.5); OnPropertyChanged(); RaiseScaleStates(); } }
    }

    private void RaiseScaleStates()
    {
        OnPropertyChanged(nameof(Scale100));
        OnPropertyChanged(nameof(Scale125));
        OnPropertyChanged(nameof(Scale150));
    }

    // ── 启动与托盘 ────────────────────────────────────────────────────────

    /// <summary>关窗 = 最小化到托盘（关掉则直接退出）。</summary>
    public bool CloseToTray
    {
        get => _ui.CloseToTray;
        set
        {
            _ui.SetCloseToTray(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseToTrayOn));
            OnPropertyChanged(nameof(CloseToTrayOff));
            Status = value ? "关窗将最小化到托盘（托盘菜单可退出）" : "关窗将直接退出程序";
        }
    }

    public bool CloseToTrayOn
    {
        get => CloseToTray;
        set { if (value) { CloseToTray = true; } }
    }

    public bool CloseToTrayOff
    {
        get => !CloseToTray;
        set { if (value) { CloseToTray = false; } }
    }

    /// <summary>开机自启（HKCU Run 键）。</summary>
    public bool LaunchAtStartup
    {
        get => _ui.LaunchAtStartup;
        set
        {
            var ok = _ui.SetLaunchAtStartup(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(LaunchAtStartupOn));
            OnPropertyChanged(nameof(LaunchAtStartupOff));
            Status = ok
                ? (value ? "已开启开机自启" : "已关闭开机自启")
                : "设置开机自启失败（见日志）";
            if (!ok)
            {
                OnPropertyChanged(nameof(LaunchAtStartup));
                OnPropertyChanged(nameof(LaunchAtStartupOn));
                OnPropertyChanged(nameof(LaunchAtStartupOff));
            }
        }
    }

    public bool LaunchAtStartupOn
    {
        get => LaunchAtStartup;
        set { if (value) { LaunchAtStartup = true; } }
    }

    public bool LaunchAtStartupOff
    {
        get => !LaunchAtStartup;
        set { if (value) { LaunchAtStartup = false; } }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    // ── 命令 ──────────────────────────────────────────────────────────────

    public AsyncRelayCommand OpenPluginDirectoryCommand { get; }

    public AsyncRelayCommand OpenDataDirectoryCommand { get; }

    public AsyncRelayCommand OpenLogDirectoryCommand { get; }

    public AsyncRelayCommand OpenConfigDirectoryCommand { get; }

    /// <summary>重新扫描插件目录（原左侧工具栏的「刷新目录」）。</summary>
    public AsyncRelayCommand RescanCommand { get; }

    /// <summary>重新加载全部插件（原左侧工具栏的「加载全部」）。</summary>
    public AsyncRelayCommand ReloadCommand { get; }

    private Task OpenFolderAsync(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"已打开：{path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开目录失败：{Path}", path);
            Status = $"打开失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
