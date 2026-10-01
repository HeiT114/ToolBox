using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Toolbox.App.Services;
using Toolbox.Contracts;
using Toolbox.PluginHost;
using Toolbox.Views.Wpf;
// WinForms 被隐式 using（托盘用），下面这些类型与 WPF / Win32 同名：显式取要用的那一个。
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Toolbox.App.ViewModels;

/// <summary>主窗口视图模型：插件列表 / 结果历史 / 日志 / 命令面板。</summary>
/// <remarks>
/// 交互约定（1.0.0-beta）：
/// <list type="bullet">
///   <item>单击插件 = 直接打开（未加载则加载，并把它的界面标签页切到前台）；</item>
///   <item>右键插件 = 单个插件的 打开 / 卸载 / 重载 / 删除；</item>
///   <item>Ctrl+K = 命令面板（搜插件、执行动作、跑工具、切主题）；</item>
///   <item>所有"执行结果"进 结果历史，可一键复制。</item>
/// </list>
/// 依然不碰协议与契约，也绝不引用任何插件对象 —— 列表里只有 PluginDescriptor 的字符串投影。
/// </remarks>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly string AppVersion = ResolveVersion();

    private readonly PluginManager _manager;
    private readonly ObservableLogSink _sink;
    private readonly ILogger<MainViewModel> _logger;
    private readonly PluginHostOptions _hostOptions;
    private readonly IConfiguration _configuration;
    private readonly ThemeService _theme;
    private readonly UiPreferences _uiPrefs;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<string> _openingIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PaletteItemViewModel> _allPaletteItems = [];

    private PluginItemViewModel? _selectedPlugin;
    private string _status = "就绪";
    private bool _isAddToolOpen;
    private bool _isRefreshing;
    private bool _suspendAutoOpen;
    private bool _isPaletteOpen;
    private bool _isRailExpanded = true;
    private bool _isLogView;
    private string _pluginFilter = string.Empty;
    private string _paletteQuery = string.Empty;
    private PaletteItemViewModel? _selectedPaletteItem;
    private int _selectedTabIndex = -1;
    private string? _pendingTabPluginId;

    public MainViewModel(
        PluginManager manager,
        ObservableLogSink sink,
        ILogger<MainViewModel> logger,
        PluginHostOptions hostOptions,
        IConfiguration configuration,
        ThemeService theme,
        UiPreferences uiPrefs)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostOptions = hostOptions ?? throw new ArgumentNullException(nameof(hostOptions));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _uiPrefs = uiPrefs ?? throw new ArgumentNullException(nameof(uiPrefs));
        _dispatcher = Application.Current.Dispatcher;

        _uiPrefs.FontScaleChanged += _ => OnPropertyChanged(nameof(FontScale));
        _uiPrefs.CloseToTrayChanged += _ => OnPropertyChanged(nameof(CloseToTray));

        // 插件状态变化可能来自线程池线程（加载/卸载都在后台跑），统一 marshal 回 UI 线程刷新。
        _manager.DescriptorChanged += (_, _) =>
        {
            if (_dispatcher.CheckAccess())
            {
                Refresh();
            }
            else
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
            }
        };

        // ⚠ 卸载协议要求：ALC 卸载**之前**必须把插件视图移出视觉树。
        //    这里同步等待 UI 线程完成释放；因此 UI 侧不能用 .Result/.Wait() 阻塞等待卸载（会死锁）。
        _manager.PluginUnloading += (_, descriptor) =>
        {
            try
            {
                if (_dispatcher.CheckAccess())
                {
                    ReleaseToolViews(descriptor.Id);
                }
                else
                {
                    _dispatcher.Invoke(() => ReleaseToolViews(descriptor.Id));
                }
            }
            catch (Exception ex)
            {
                // 释放失败会让 ALC 回收失败（并留下诊断），但不能卡死卸载流程
                _logger.LogWarning(ex, "卸载前释放插件 {PluginId} 的视图失败。", descriptor.Id);
            }
        };

        RefreshCommand = new AsyncRelayCommand(() =>
        {
            Refresh();
            return Task.CompletedTask;
        });

        LoadAllCommand = new AsyncRelayCommand(LoadAllAsync);
        OpenPluginCommand = new AsyncRelayCommand<PluginItemViewModel>(OpenPluginAsync, item => item is not null);
        UnloadPluginCommand = new AsyncRelayCommand<PluginItemViewModel>(UnloadPluginAsync, item => item is not null);
        ReloadPluginCommand = new AsyncRelayCommand<PluginItemViewModel>(ReloadPluginAsync, item => item is not null);
        DeletePluginCommand = new AsyncRelayCommand<PluginItemViewModel>(DeletePluginAsync, item => item is not null);
        OpenPluginFolderCommand = new AsyncRelayCommand(OpenPluginFolderAsync);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        AddPackageCommand = new AsyncRelayCommand(AddPackageAsync);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync);
        TogglePaletteCommand = new AsyncRelayCommand(() =>
        {
            TogglePalette();
            return Task.CompletedTask;
        });
        ToggleThemeCommand = new AsyncRelayCommand(() =>
        {
            _theme.Toggle();
            return Task.CompletedTask;
        });
        AboutCommand = new AsyncRelayCommand(OpenAboutAsync);
        OpenMarketCommand = new AsyncRelayCommand(OpenMarketAsync);
        CopyResultCommand = new AsyncRelayCommand<object?>(CopyResultAsync);
        ClearResultCommand = new AsyncRelayCommand(() =>
        {
            ResultHistory.Clear();
            Status = "结果历史已清空";
            return Task.CompletedTask;
        });
        ExportHistoryCommand = new AsyncRelayCommand(ExportHistoryAsync);

        _theme.ThemeChanged += _ => OnPropertyChanged(nameof(IsDarkTheme));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── 顶部栏 ────────────────────────────────────────────────────────────

    /// <summary>当前 Windows 用户名（头像用；只读字符串，不涉及任何插件对象）。</summary>
    public string UserName { get; } = string.IsNullOrWhiteSpace(Environment.UserName) ? "user" : Environment.UserName;

    /// <summary>头像里的首字母。</summary>
    public string UserInitial => char.ToUpperInvariant(UserName[0]).ToString();

    public string UserAccountLine =>
        $"{Environment.UserDomainName}\\{UserName} @ {Environment.MachineName}";

    public string UserTooltip => $"当前用户：{UserAccountLine}（点击查看详情）";

    public string UserRuntimeLine =>
        $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}";

    /// <summary>顶部栏副标题（带版本号）。</summary>
    public string HeaderSubtitle =>
        $"插件化工具箱 · 跨 AssemblyLoadContext 动态加载 · v{AppVersion}";

    /// <summary>状态栏右侧的环境徽标。</summary>
    public string EnvironmentBadge => $".NET {Environment.Version} · {RuntimeInformation.OSArchitecture}";

    /// <summary>版本号（InformationalVersion，如 1.0.0-beta）。</summary>
    public string VersionText => $"v{AppVersion}";

    // ── 集合 ──────────────────────────────────────────────────────────────

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    /// <summary>按 <see cref="PluginFilter"/> 过滤后实际显示在列表里的插件（UIA/选中都作用于它）。</summary>
    public ObservableCollection<PluginItemViewModel> VisiblePlugins { get; } = [];

    /// <summary>带界面的插件工具的标签页。</summary>
    public ObservableCollection<ToolTabViewModel> ToolTabs { get; } = [];

    public ObservableCollection<LogEntry> Logs => _sink.Entries;

    /// <summary>结果历史（新的在最上面，最多 50 条）。</summary>
    public ObservableCollection<ResultEntryViewModel> ResultHistory { get; } = [];

    // ── 命令 ──────────────────────────────────────────────────────────────

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand LoadAllCommand { get; }

    /// <summary>打开插件（右键菜单 / 命令面板）：未加载则加载，并切到它的界面标签页。</summary>
    public AsyncRelayCommand<PluginItemViewModel> OpenPluginCommand { get; }

    /// <summary>卸载单个插件（右键菜单 / 命令面板）。</summary>
    public AsyncRelayCommand<PluginItemViewModel> UnloadPluginCommand { get; }

    /// <summary>重载单个插件（右键菜单 / 命令面板）。</summary>
    public AsyncRelayCommand<PluginItemViewModel> ReloadPluginCommand { get; }

    /// <summary>从磁盘删除单个插件（右键菜单 / 命令面板，带确认）。</summary>
    public AsyncRelayCommand<PluginItemViewModel> DeletePluginCommand { get; }

    public AsyncRelayCommand OpenPluginFolderCommand { get; }

    /// <summary>打开设置窗口（小齿轮）。设置窗口只读展示生效中的宿主配置 + 主题切换。</summary>
    public AsyncRelayCommand OpenSettingsCommand { get; }

    /// <summary>「添加工具」→ 选择插件包（.zip / .dll）。</summary>
    public AsyncRelayCommand AddPackageCommand { get; }

    /// <summary>「添加工具」→ 选择插件文件夹（目录插件）。</summary>
    public AsyncRelayCommand AddFolderCommand { get; }

    /// <summary>打开关于窗口（版本 / 运行时 / 说明）。</summary>
    public AsyncRelayCommand AboutCommand { get; }

    /// <summary>打开插件市场窗口（浏览 market/ 并安装）。</summary>
    public AsyncRelayCommand OpenMarketCommand { get; }

    /// <summary>左侧图文栏是否展开（收起后只剩图标）。</summary>
    public bool IsRailExpanded
    {
        get => _isRailExpanded;
        set => SetField(ref _isRailExpanded, value);
    }

    /// <summary>中间面板是否显示"日志"视图（与插件界面互斥；只是 Visibility 切换，控件始终留在树上）。</summary>
    public bool IsLogView
    {
        get => _isLogView;
        set => SetField(ref _isLogView, value);
    }

    /// <summary>Ctrl+K 面板开关（顶部搜索胶囊也用它）。</summary>
    public AsyncRelayCommand TogglePaletteCommand { get; }

    /// <summary>浅色 ⇄ 深色。</summary>
    public AsyncRelayCommand ToggleThemeCommand { get; }

    /// <summary>复制结果历史里的一条（参数为空则复制全部）。</summary>
    public AsyncRelayCommand<object?> CopyResultCommand { get; }

    public AsyncRelayCommand ClearResultCommand { get; }

    /// <summary>把结果历史导出成 txt 文件。</summary>
    public AsyncRelayCommand ExportHistoryCommand { get; }

    // ── 状态 ──────────────────────────────────────────────────────────────

    /// <summary>「添加工具」浮层是否展开（与 ToggleButton.IsChecked 双向绑定）。</summary>
    public bool IsAddToolOpen
    {
        get => _isAddToolOpen;
        set => SetField(ref _isAddToolOpen, value);
    }

    /// <summary>Ctrl+K 命令面板是否打开。</summary>
    public bool IsPaletteOpen
    {
        get => _isPaletteOpen;
        set
        {
            if (!SetField(ref _isPaletteOpen, value))
            {
                return;
            }

            if (value)
            {
                _paletteQuery = string.Empty;
                OnPropertyChanged(nameof(PaletteQuery));
                RebuildPaletteItems();
            }
        }
    }

    /// <summary>命令面板搜索词。</summary>
    public string PaletteQuery
    {
        get => _paletteQuery;
        set
        {
            if (SetField(ref _paletteQuery, value ?? string.Empty))
            {
                RebuildPaletteItems();
            }
        }
    }

    /// <summary>命令面板条目（已按搜索词过滤）。</summary>
    public ObservableCollection<PaletteItemViewModel> PaletteItems { get; } = [];

    public PaletteItemViewModel? SelectedPaletteItem
    {
        get => _selectedPaletteItem;
        set => SetField(ref _selectedPaletteItem, value);
    }

    /// <summary>当前主题是否为深色（设置窗口两个胶囊用）。</summary>
    public bool IsDarkTheme
    {
        get => _theme.Current == AppTheme.Dark;
        set
        {
            if (value == (_theme.Current == AppTheme.Dark))
            {
                return;
            }

            _theme.Apply(value ? AppTheme.Dark : AppTheme.Light);
            OnPropertyChanged();
        }
    }

    public PluginItemViewModel? SelectedPlugin
    {
        get => _selectedPlugin;
        set
        {
            if (!SetField(ref _selectedPlugin, value))
            {
                return;
            }

            // 单击 = 直接打开：只响应"用户点击"；目录刷新与过滤重建（程序化选中）不算。
            if (!_isRefreshing && !_suspendAutoOpen && value is not null && _openingIds.Add(value.Id))
            {
                _ = OpenAndTrackAsync(value);
            }
        }
    }

    /// <summary>插件列表过滤词（按显示名 / Id 匹配）。</summary>
    public string PluginFilter
    {
        get => _pluginFilter;
        set
        {
            if (SetField(ref _pluginFilter, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>界面字号缩放（1.0 / 1.25 / 1.5），持久化在 ui.json。</summary>
    public double FontScale
    {
        get => _uiPrefs.FontScale;
        set => _uiPrefs.SetFontScale(value);
    }

    /// <summary>关闭窗口时是否最小化到托盘（关掉则直接退出）。</summary>
    public bool CloseToTray
    {
        get => _uiPrefs.CloseToTray;
        set => _uiPrefs.SetCloseToTray(value);
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusLine));
            }
        }
    }

    /// <summary>已加载插件数（状态栏用）。</summary>
    public int LoadedPluginCount => Plugins.Count(p => p.State == PluginState.Loaded);

    /// <summary>状态栏文本：已加载插件数 / 视图标签页数 / 最近一次操作结果。</summary>
    public string StatusLine =>
        $"已加载插件：{LoadedPluginCount} / {Plugins.Count}　·　视图标签页：{ToolTabs.Count}　·　{Status}";

    /// <summary>插件界面 TabControl 的选中下标（打开插件后由它切到对应标签页）。</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            var normalized = value;
            if (normalized < -1)
            {
                normalized = -1;
            }

            if (normalized >= ToolTabs.Count)
            {
                normalized = ToolTabs.Count - 1;
            }

            SetField(ref _selectedTabIndex, normalized);
        }
    }

    // ── 生命周期 ──────────────────────────────────────────────────────────

    /// <summary>窗口显示后调用：刷新目录并尝试加载全部插件。</summary>
    public async Task InitializeAsync()
    {
        Refresh();
        await LoadAllAsync();

        // 启动即有一个带界面的工具时，直接停在第一个标签页上
        if (ToolTabs.Count > 0 && SelectedTabIndex < 0)
        {
            SelectedTabIndex = 0;
        }
    }

    /// <summary>重新读取插件目录（不加载），并重建列表与工具标签页。</summary>
    public void Refresh()
    {
        var selectedId = SelectedPlugin?.Id;

        _isRefreshing = true;
        try
        {
            _manager.RefreshCatalog();

            Plugins.Clear();
            foreach (var descriptor in _manager.Descriptors)
            {
                Plugins.Add(new PluginItemViewModel(descriptor));
            }

            SelectedPlugin = Plugins.FirstOrDefault(p => p.Id == selectedId) ?? Plugins.FirstOrDefault();

            // 过滤结果同步重建（过滤词可能把当前选中项滤掉）
            ApplyFilter();
        }
        finally
        {
            _isRefreshing = false;
        }

        RebuildToolTabs();

        Status = $"发现 {Plugins.Count} 个插件，已加载 {LoadedPluginCount} 个";
    }

    // ── 插件标签页 ────────────────────────────────────────────────────────

    /// <summary>
    /// 重建"带界面的工具"标签页：先释放旧视图（移出视觉树 + 回调插件），再按已加载插件重建。
    /// </summary>
    private void RebuildToolTabs()
    {
        // 已加载插件 → 需要哪些视图标签页
        var desired = new List<(PluginDescriptor Descriptor, ToolboxToolInfo Info)>();
        foreach (var descriptor in _manager.Descriptors.Where(d => d.State == PluginState.Loaded))
        {
            foreach (var info in _manager.GetToolInfos(descriptor.Id))
            {
                if (info.HasView)
                {
                    desired.Add((descriptor, info));
                }
            }
        }

        // 需要的东西没变就不要动：避免每次状态刷新都把插件视图拆了重建
        var desiredKeys = desired.Select(d => $"{d.Descriptor.Id}/{d.Info.Id}").ToArray();
        var currentKeys = ToolTabs.Select(t => $"{t.PluginId}/{t.ToolId}").ToArray();
        if (desiredKeys.SequenceEqual(currentKeys, StringComparer.OrdinalIgnoreCase))
        {
            TryActivatePendingTab();
            return;
        }

        foreach (var tab in ToolTabs)
        {
            tab.Release();
        }

        ToolTabs.Clear();

        foreach (var (descriptor, info) in desired)
        {
            if (!PluginToolViewHost.TryCreate(_manager, descriptor.Id, info.Id, out var host, out var error) || host is null)
            {
                _logger.LogWarning("创建插件视图失败：{PluginId}/{ToolId} → {Error}", descriptor.Id, info.Id, error);
                continue;
            }

            ToolTabs.Add(new ToolTabViewModel($"{descriptor.DisplayName} · {info.DisplayName}", host, descriptor.Id, info.Id));
        }

        OnPropertyChanged(nameof(StatusLine));
        TryActivatePendingTab();
    }

    /// <summary>把某个插件的所有视图标签页移出视觉树并移除（卸载前必须调用）。</summary>
    private void ReleaseToolViews(string pluginId)
    {
        for (var i = ToolTabs.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(ToolTabs[i].PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ToolTabs[i].Release();
            ToolTabs.RemoveAt(i);
        }

        OnPropertyChanged(nameof(StatusLine));
    }

    /// <summary>标记"打开后要切到这个插件的标签页"。</summary>
    private void ActivateTab(string pluginId)
    {
        _pendingTabPluginId = pluginId;
        TryActivatePendingTab();
    }

    private void TryActivatePendingTab()
    {
        if (_pendingTabPluginId is null)
        {
            return;
        }

        for (var i = 0; i < ToolTabs.Count; i++)
        {
            if (!string.Equals(ToolTabs[i].PluginId, _pendingTabPluginId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SelectedTabIndex = i;
            _pendingTabPluginId = null;
            return;
        }
    }

    // ── 插件操作 ──────────────────────────────────────────────────────────

    private async Task LoadAllAsync()
    {
        try
        {
            Status = "正在加载全部插件…";
            var loaded = await _manager.LoadAllAsync();
            var failed = Plugins.Count(p => p.State == PluginState.Failed);
            Status = $"加载完成：成功 {loaded.Count} 个，失败 {failed} 个";
            AddResult("加载全部插件", Status, failed == 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载全部插件失败。");
            Status = $"加载失败：{ex.Message}";
            AddResult("加载全部插件", ex.Message, false);
        }
    }

    private async Task OpenAndTrackAsync(PluginItemViewModel item)
    {
        try
        {
            await OpenPluginAsync(item);
        }
        finally
        {
            _openingIds.Remove(item.Id);
        }
    }

    /// <summary>单击打开：未加载则加载，然后把它的界面标签页切到前台。</summary>
    private async Task OpenPluginAsync(PluginItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (item.State != PluginState.Loaded)
            {
                Status = $"正在打开 {item.Id}…";
                var descriptor = await _manager.LoadAsync(item.Descriptor.SourcePath);
                AddResult($"打开插件 {descriptor.Id}", $"v{descriptor.Version} 已加载 · 工具：{string.Join(", ", descriptor.ToolNames)}", true);
                Status = $"插件 {descriptor.Id} 已打开";
            }

            ActivateTab(item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开插件 {PluginId} 失败。", item.Id);
            AddResult($"打开插件 {item.Id}", ex.Message, false);
            Status = $"打开失败：{ex.Message}";
        }
    }

    private async Task UnloadPluginAsync(PluginItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // 卸载会做强制 GC 验证，放到线程池上执行，UI 不卡（PluginManager 内部已经这么做了）。
        var report = await _manager.UnloadAsync(item.Id);
        var text = report.ContextCollected
            ? $"插件 {report.PluginId} 已卸载并回收（{report.Elapsed.TotalMilliseconds:F0} ms）"
            : $"插件 {report.PluginId} 已 Unload，但 ALC 未被回收：仍被引用（见日志）";
        Status = text;
        AddResult($"卸载 {report.PluginId}", text, report.ContextCollected);
    }

    private async Task ReloadPluginAsync(PluginItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            Status = $"正在重载 {item.Id}…";
            var descriptor = await _manager.ReloadAsync(item.Id);
            Status = $"插件 {descriptor.Id} 已重载（新 ALC）";
            AddResult($"重载 {descriptor.Id}", Status, true);
            ActivateTab(descriptor.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重载插件 {PluginId} 失败。", item.Id);
            Status = $"重载失败：{ex.Message}";
            AddResult($"重载 {item.Id}", ex.Message, false);
        }
    }

    /// <summary>删除插件：确认 → 已加载则先卸载 → 从磁盘删掉目录/zip → 刷新。</summary>
    private async Task DeletePluginAsync(PluginItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var sourcePath = item.Descriptor.SourcePath;
        var answer = MessageBox.Show(
            $"确定删除插件「{item.DisplayName}」（{item.Id}）吗？\n\n" +
            $"将卸载并从磁盘删除：\n{sourcePath}\n\n此操作不可恢复。",
            "删除插件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (item.State == PluginState.Loaded)
        {
            await _manager.UnloadAsync(item.Id);
        }

        try
        {
            if (Directory.Exists(sourcePath))
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            else if (File.Exists(sourcePath))
            {
                File.Delete(sourcePath);
            }
            else
            {
                throw new FileNotFoundException("插件来源已不存在。", sourcePath);
            }

            Status = $"插件 {item.Id} 已删除";
            AddResult($"删除插件 {item.Id}", $"已从磁盘移除：{sourcePath}", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除插件 {PluginId} 的文件失败。", item.Id);
            Status = $"删除失败：{ex.Message}";
            AddResult($"删除插件 {item.Id}", ex.Message, false);
        }

        Refresh();
    }

    /// <summary>命令面板里"运行工具"：宿主侧调用，结果进历史。</summary>
    private async Task RunToolAsync(string pluginId, string toolId)
    {
        Status = $"正在运行 {pluginId}/{toolId}…";
        var result = await _manager.InvokeToolAsync(pluginId, toolId, new Dictionary<string, string?>());

        var text = result.Success
            ? $"✔ {pluginId}/{toolId} → {result.Output}"
            : $"✘ {pluginId}/{toolId} → {result.Error}";

        var body = result.Success ? result.Output : result.Error;
        AddResult($"{toolId}（{pluginId}）", text, result.Success, body);
        Status = result.Success ? "工具执行完成" : "工具执行失败";
    }

    // ── 命令面板 ──────────────────────────────────────────────────────────

    public void TogglePalette() => IsPaletteOpen = !IsPaletteOpen;

    /// <summary>↑ / ↓ 移动面板选中项。</summary>
    public void MovePaletteSelection(int delta)
    {
        if (PaletteItems.Count == 0)
        {
            return;
        }

        var index = SelectedPaletteItem is null ? -1 : PaletteItems.IndexOf(SelectedPaletteItem);
        index = Math.Clamp(index + delta, 0, PaletteItems.Count - 1);
        SelectedPaletteItem = PaletteItems[index];
    }

    /// <summary>执行面板当前选中项（先关面板再执行）。</summary>
    public async Task RunPaletteSelectedAsync()
    {
        var item = SelectedPaletteItem;
        IsPaletteOpen = false;

        if (item is null)
        {
            return;
        }

        try
        {
            await item.Action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "命令面板执行 {Command} 失败。", item.Title);
            Status = $"命令执行失败：{ex.Message}";
            AddResult(item.Title, ex.Message, false);
        }
    }

    private void RebuildPaletteItems()
    {
        _allPaletteItems.Clear();

        void Add(string title, string subtitle, string search, Func<Task> action) =>
            _allPaletteItems.Add(new PaletteItemViewModel(title, subtitle, search, action));

        // 宿主动作
        Add("刷新插件目录", "重新扫描 plugins/，不加载", "refresh 刷新 扫描 目录", () =>
        {
            Refresh();
            return Task.CompletedTask;
        });
        Add("加载全部插件", "加载插件目录下的所有插件", "load all 加载全部", LoadAllAsync);
        Add("添加插件包（zip / dll）…", "选择文件装进插件目录并立即加载", "add install 添加 安装 zip dll", AddPackageAsync);
        Add("添加插件文件夹…", "选择文件夹装进插件目录并立即加载", "add folder 添加 文件夹", AddFolderAsync);
        Add("打开插件目录", "在资源管理器里打开 plugins/", "open folder 插件目录", OpenPluginFolderAsync);
        Add("插件市场", "浏览 market/ 并安装插件包", "market 市场 安装 插件市场", OpenMarketAsync);
        Add("打开设置", "宿主配置只读视图 · 主题切换 · 重新扫描/重新加载", "settings set 设置 配置", OpenSettingsAsync);
        Add("关于", "版本 / 运行时 / 说明", "about version 关于 版本", OpenAboutAsync);
        Add("查看日志", "切换中间面板显示实时日志", "log logs 日志 查看日志", () =>
        {
            IsLogView = true;
            Status = "已切换到日志视图（再点左侧「日志」返回插件界面）";
            return Task.CompletedTask;
        });
        Add("切换主题", _theme.Current == AppTheme.Light ? "当前：浅色 → 深色" : "当前：深色 → 浅色",
            "theme 主题 深色 浅色 dark light", () =>
            {
                _theme.Toggle();
                Status = _theme.Current == AppTheme.Dark ? "已切换到深色主题" : "已切换到浅色主题";
                return Task.CompletedTask;
            });
        Add("退出程序", "卸载全部插件并退出（关闭窗口则是最小化到托盘）", "exit quit 退出 关闭", () =>
        {
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ForceClose();
            }

            return Task.CompletedTask;
        });

        // 每个插件：打开 / 卸载 / 重载 / 删除 / 运行工具
        foreach (var plugin in Plugins)
        {
            var label = $"{plugin.DisplayName}（{plugin.Id}）";

            Add($"打开 {label}", $"当前：{plugin.StateText}", $"{plugin.Id} {plugin.DisplayName} 打开 open",
                () => OpenPluginAsync(plugin));

            if (plugin.State == PluginState.Loaded)
            {
                Add($"卸载 {label}", "卸载并验证 ALC 回收", $"{plugin.Id} {plugin.DisplayName} 卸载 unload",
                    () => UnloadPluginAsync(plugin));
                Add($"重载 {label}", "卸载后立即重新加载（新 ALC）", $"{plugin.Id} {plugin.DisplayName} 重载 reload",
                    () => ReloadPluginAsync(plugin));

                foreach (var toolId in plugin.ToolNames)
                {
                    Add($"运行 {label} · {toolId}", "宿主侧执行，结果写入历史",
                        $"{plugin.Id} {plugin.DisplayName} {toolId} 运行 run 执行",
                        () => RunToolAsync(plugin.Id, toolId));
                }
            }

            Add($"删除 {label}…", "从磁盘删除插件文件（带确认）", $"{plugin.Id} {plugin.DisplayName} 删除 delete remove",
                () => DeletePluginAsync(plugin));
        }

        var filtered = _allPaletteItems.Where(i => i.Matches(PaletteQuery)).ToList();

        PaletteItems.Clear();
        foreach (var item in filtered)
        {
            PaletteItems.Add(item);
        }

        SelectedPaletteItem = PaletteItems.Count > 0 ? PaletteItems[0] : null;
    }

    // ── 工具目录 / 添加工具 ───────────────────────────────────────────────

    private Task OpenPluginFolderAsync()
    {
        try
        {
            var root = _manager.PluginRoot;
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开插件目录失败。");
            Status = $"打开插件目录失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>「添加工具」→ 选择插件包（.zip / .dll）→ 安装进插件目录 → 立即加载。</summary>
    private Task AddPackageAsync()
    {
        IsAddToolOpen = false;

        var dialog = new OpenFileDialog
        {
            Title = "选择插件包（.zip / .dll）",
            Filter = "插件包 (*.zip;*.dll)|*.zip;*.dll|zip 插件包 (*.zip)|*.zip|插件程序集 (*.dll)|*.dll|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? InstallAsync(dialog.FileNames) : Task.CompletedTask;
    }

    /// <summary>「添加工具」→ 选择插件文件夹（目录插件）→ 安装进插件目录 → 立即加载。</summary>
    private Task AddFolderAsync()
    {
        IsAddToolOpen = false;

        var dialog = new OpenFolderDialog
        {
            Title = "选择插件文件夹",
            Multiselect = true,
        };

        return dialog.ShowDialog() == true ? InstallAsync(dialog.FolderNames) : Task.CompletedTask;
    }

    /// <summary>
    /// 把用户选中的插件来源装进插件目录并立即加载。
    /// </summary>
    /// <remarks>
    /// 安装形态按宿主的发现规则来（<c>PluginLoader.Discover</c> 只认"含 dll 的子目录"和"顶层 *.zip"）：
    /// <list type="bullet">
    ///   <item><c>.zip</c> → <c>plugins/&lt;包名&gt;.zip</c>（zip 插件，影子解包）；</item>
    ///   <item><c>.dll</c> → <c>plugins/&lt;dll 名&gt;/&lt;dll 名&gt;.dll</c>（落成目录插件，重启后也能被发现）；</item>
    ///   <item>文件夹 → <c>plugins/&lt;文件夹名&gt;/</c>（目录插件，整棵拷贝）。</item>
    /// </list>
    /// 失败（例如目标 dll 仍被旧 ALC 占用）逐条记录，不影响其它包。
    /// 主窗口（添加工具）与插件市场共用这一条安装路径。
    /// </remarks>
    public async Task InstallAsync(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        var root = _manager.PluginRoot;
        Directory.CreateDirectory(root);

        var installed = new List<string>();
        var problems = new List<string>();

        foreach (var source in sources)
        {
            try
            {
                installed.Add(InstallOne(source, root));
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(source)}（{ex.Message}）");
                _logger.LogWarning(ex, "安装插件来源 {Source} 失败。", source);
            }
        }

        Status = $"已安装 {installed.Count} 个插件来源，正在加载…";
        Refresh();

        var loaded = 0;
        foreach (var path in installed)
        {
            try
            {
                var descriptor = await _manager.LoadAsync(path);
                loaded++;
                Status = $"已添加并加载 {descriptor.Id}（工具：{string.Join(", ", descriptor.ToolNames)}）";
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(path)}（{ex.Message}）");
                _logger.LogError(ex, "加载新添加的插件 {Path} 失败。", path);
            }
        }

        Refresh();

        Status = problems.Count == 0
            ? $"添加工具完成：安装 {installed.Count} 个，加载成功 {loaded} 个"
            : $"添加工具完成：安装 {installed.Count} 个，加载 {loaded} 个，失败 {problems.Count} 个 → {string.Join("；", problems)}";

        AddResult("添加工具", Status, problems.Count == 0);
    }

    /// <summary>按来源形态安装一个插件来源，返回装好后的路径。</summary>
    private static string InstallOne(string source, string pluginRoot)
    {
        if (Directory.Exists(source))
        {
            var target = Path.Combine(pluginRoot, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            CopyDirectory(source, target);
            return target;
        }

        var fileName = Path.GetFileName(source);

        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var target = Path.Combine(pluginRoot, fileName);
            File.Copy(source, target, overwrite: true);
            return target;
        }

        // 单个 dll：宿主只把"含 dll 的目录"当作目录插件，所以给它建一个同名子目录。
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var directory = Path.Combine(pluginRoot, baseName);
        Directory.CreateDirectory(directory);
        File.Copy(source, Path.Combine(directory, fileName), overwrite: true);
        return directory;
    }

    /// <summary>递归拷贝目录（覆盖同名文件）。</summary>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    // ── 设置 / 结果历史 ───────────────────────────────────────────────────

    private Task OpenSettingsAsync()
    {
        try
        {
            var settings = new SettingsViewModel(_hostOptions, _manager, _configuration, _logger, _theme, _uiPrefs, Refresh);
            var window = new SettingsWindow
            {
                DataContext = settings,
                Owner = Application.Current?.MainWindow,
            };

            // 模态：设置窗口只是"看一眼"，不参与插件生命周期，也不写回任何配置。
            _ = window.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开设置窗口失败。");
            Status = $"打开设置失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>打开关于窗口（静态信息，不含任何插件对象）。</summary>
    private Task OpenAboutAsync()
    {
        try
        {
            var window = new AboutWindow
            {
                Owner = Application.Current?.MainWindow,
            };
            _ = window.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开关于窗口失败。");
            Status = $"打开关于失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>打开插件市场窗口（市场视图模型复用本实例的安装路径）。</summary>
    private Task OpenMarketAsync()
    {
        try
        {
            var market = new MarketViewModel(this, _manager);
            var window = new MarketWindow
            {
                DataContext = market,
                Owner = Application.Current?.MainWindow,
            };
            _ = window.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开插件市场失败。");
            Status = $"打开插件市场失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>往结果历史头部加一条（最多留 50 条）。</summary>
    private void AddResult(string title, string detail, bool success, string? fullText = null)
    {
        ResultHistory.Insert(0, new ResultEntryViewModel(title, detail, success, fullText));

        while (ResultHistory.Count > 50)
        {
            ResultHistory.RemoveAt(ResultHistory.Count - 1);
        }
    }

    private Task CopyResultAsync(object? parameter)
    {
        try
        {
            if (parameter is ResultEntryViewModel entry)
            {
                Clipboard.SetText(entry.FullText);
                Status = $"已复制：{entry.Title}";
            }
            else if (ResultHistory.Count > 0)
            {
                Clipboard.SetText(FormatHistory());
                Status = $"已复制全部 {ResultHistory.Count} 条结果";
            }
            else
            {
                Status = "结果历史是空的，没有可复制的内容";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复制结果失败。");
            Status = $"复制失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>导出结果历史为 txt（用户取消不报错）。</summary>
    private Task ExportHistoryAsync()
    {
        if (ResultHistory.Count == 0)
        {
            Status = "结果历史是空的，没有可导出的内容";
            return Task.CompletedTask;
        }

        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出结果历史",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                FileName = $"toolbox-results-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExt = ".txt",
                AddExtension = true,
            };

            if (dialog.ShowDialog() != true)
            {
                return Task.CompletedTask;
            }

            File.WriteAllText(dialog.FileName, FormatHistory(), new System.Text.UTF8Encoding(true));
            Status = $"已导出 {ResultHistory.Count} 条结果 → {dialog.FileName}";
            AddResult("导出结果历史", $"写入 {dialog.FileName}（{ResultHistory.Count} 条）", true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "导出结果历史失败。");
            Status = $"导出失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>全部结果的可读文本（复制/导出共用）。</summary>
    private string FormatHistory() => string.Join(
        Environment.NewLine + new string('-', 48) + Environment.NewLine,
        ResultHistory.Select(r => $"[{r.Time}] {r.Title}{Environment.NewLine}{r.FullText}"));

    /// <summary>
    /// 按 <see cref="PluginFilter"/> 重建可见插件集合；
    /// 重建过程中的选中回写是程序化的，必须挂起"单击打开"逻辑。
    /// </summary>
    private void ApplyFilter()
    {
        _suspendAutoOpen = true;
        try
        {
            var keep = SelectedPlugin;

            VisiblePlugins.Clear();
            foreach (var plugin in Plugins)
            {
                if (MatchesFilter(plugin))
                {
                    VisiblePlugins.Add(plugin);
                }
            }

            if (keep is not null && VisiblePlugins.Contains(keep))
            {
                SelectedPlugin = keep;
            }
            else if (SelectedPlugin is null && VisiblePlugins.Count > 0)
            {
                SelectedPlugin = VisiblePlugins[0];
            }
            else if (keep is not null && SelectedPlugin is null && VisiblePlugins.Count == 0)
            {
                SelectedPlugin = null;
            }
        }
        finally
        {
            _suspendAutoOpen = false;
        }
    }

    private bool MatchesFilter(PluginItemViewModel plugin)
    {
        if (string.IsNullOrWhiteSpace(_pluginFilter))
        {
            return true;
        }

        return plugin.DisplayName.Contains(_pluginFilter, StringComparison.OrdinalIgnoreCase)
               || plugin.Id.Contains(_pluginFilter, StringComparison.OrdinalIgnoreCase);
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static string ResolveVersion()
    {
        try
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
