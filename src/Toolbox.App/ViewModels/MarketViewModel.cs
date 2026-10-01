using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using Toolbox.App.Services;
using Toolbox.PluginHost;
// WinForms 被隐式 using：取 Win32 的文件对话框。
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Toolbox.App.ViewModels;

/// <summary>
/// 插件市场窗口的视图模型：浏览 <c>market/</c>、安装进 <c>plugins/</c> 并立即加载。
/// </summary>
/// <remarks>
/// 安装动作**复用主窗口的 InstallAsync**（同一份拷贝/加载逻辑与结果历史），
/// 本类只负责市场侧的目录扫描、种子播种与状态展示；不引用任何插件对象。
/// </remarks>
public sealed class MarketViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PluginManager _manager;
    private string _status = string.Empty;

    public MarketViewModel(MainViewModel owner, PluginManager manager)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

        RefreshCommand = new AsyncRelayCommand(() =>
        {
            Refresh();
            return Task.CompletedTask;
        });
        InstallCommand = new AsyncRelayCommand<MarketItemViewModel>(InstallAsync, item => item is not null && !item.IsInstalled);
        InstallFromFileCommand = new AsyncRelayCommand(InstallFromFileAsync);
        OpenMarketFolderCommand = new AsyncRelayCommand(() =>
        {
            try
            {
                Directory.CreateDirectory(MarketCatalog.MarketDirectory);
                Process.Start(new ProcessStartInfo(MarketCatalog.MarketDirectory) { UseShellExecute = true });
                Status = $"已打开市场目录：{MarketCatalog.MarketDirectory}";
            }
            catch (Exception ex)
            {
                Status = $"打开市场目录失败：{ex.Message}";
            }

            return Task.CompletedTask;
        });

        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MarketItemViewModel> Items { get; } = [];

    public string MarketDirectory => MarketCatalog.MarketDirectory;

    public string Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand<MarketItemViewModel> InstallCommand { get; }

    public AsyncRelayCommand InstallFromFileCommand { get; }

    public AsyncRelayCommand OpenMarketFolderCommand { get; }

    /// <summary>扫描市场目录；空市场时先从已安装的目录插件播种一个可安装 zip。</summary>
    private void Refresh()
    {
        try
        {
            if (MarketCatalog.IsEmpty())
            {
                TrySeed();
            }

            var installedIds = _owner.Plugins
                .Select(p => p.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Items.Clear();
            foreach (var entry in MarketCatalog.Scan())
            {
                Items.Add(new MarketItemViewModel(
                    entry.Id,
                    entry.DisplayName,
                    entry.Version,
                    entry.Description,
                    entry.SourcePath,
                    entry.KindLabel,
                    installedIds.Contains(entry.Id)));
            }

            Status = Items.Count == 0
                ? "市场目录为空：把插件包放进 market/，或用「从文件安装」。"
                : $"市场目录 {MarketDirectory}：{Items.Count} 个可安装插件";
        }
        catch (Exception ex)
        {
            Status = $"扫描市场失败：{ex.Message}";
        }
    }

    /// <summary>从已安装的目录插件打一个种子 zip（id 加 -market 后缀，避免与本体撞车）。</summary>
    private void TrySeed()
    {
        try
        {
            var source = _owner.Plugins.FirstOrDefault(p =>
                p.SourceKind == PluginSourceKind.Directory &&
                File.Exists(Path.Combine(p.Descriptor.SourcePath, "plugin.json")));

            if (source is not null)
            {
                MarketCatalog.SeedFromDirectory(source.Descriptor.SourcePath);
            }
        }
        catch (Exception ex)
        {
            _ = ex; // 播种失败不影响"空市场提示 + 从文件安装"的兜底路径
        }
    }

    private async Task InstallAsync(MarketItemViewModel? item)
    {
        if (item is null || item.IsInstalled)
        {
            return;
        }

        try
        {
            Status = $"正在安装 {item.DisplayName}…";
            await _owner.InstallAsync(new[] { item.SourcePath });
            SyncInstalledState();
            Status = Items.Any(i => i.IsInstalled && i.Id == item.Id)
                ? $"已安装并加载 {item.Id}"
                : $"安装 {item.Id} 未完成（详见结果历史/日志）";
        }
        catch (Exception ex)
        {
            Status = $"安装失败：{ex.Message}";
        }
    }

    private async Task InstallFromFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择插件包（.zip / .dll）",
            Filter = "插件包 (*.zip;*.dll)|*.zip;*.dll|zip 插件包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _owner.InstallAsync(dialog.FileNames);
        SyncInstalledState();
        Status = $"从文件安装完成（{dialog.FileNames.Length} 个来源）";
    }

    private void SyncInstalledState()
    {
        var installedIds = _owner.Plugins
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Items)
        {
            item.IsInstalled = installedIds.Contains(item.Id);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
}
