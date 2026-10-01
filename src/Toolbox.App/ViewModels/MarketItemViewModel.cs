namespace Toolbox.App.ViewModels;

/// <summary>插件市场里的一行。</summary>
public sealed class MarketItemViewModel(
    string id,
    string displayName,
    string? version,
    string description,
    string sourcePath,
    string kindLabel,
    bool installed)
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "版本 -" : $"v{Version}";

    public string? Version { get; } = version;

    public string Description { get; } = string.IsNullOrWhiteSpace(description) ? "（清单未提供描述）" : description;

    /// <summary>安装来源（市场目录里的 zip 或子目录，纯路径字符串）。</summary>
    public string SourcePath { get; } = sourcePath;

    public string MetaLine => $"{Id} · {VersionText} · {kindLabel}";

    public string SourceDisplay => System.IO.Path.GetFileName(SourcePath);

    public bool IsInstalled
    {
        get => _installed;
        set
        {
            if (_installed == value)
            {
                return;
            }

            _installed = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsInstalled)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(InstallLabel)));
        }
    }

    private bool _installed = installed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>按钮文案（已安装后变灰并改字）。</summary>
    public string InstallLabel => IsInstalled ? "已安装" : "安装";
}
