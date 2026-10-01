using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Toolbox.App.Services;

/// <summary>
/// 界面级偏好（与主题偏好分开）：字号缩放、关窗行为、开机自启。
/// 持久化到 <c>%LOCALAPPDATA%\Toolbox\ui.json</c>（按用户，不需要管理员）。
/// </summary>
public sealed class UiPreferences
{
    private const double MinScale = 1.0;
    private const double MaxScale = 1.5;

    private readonly ILogger<UiPreferences> _logger;
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Toolbox",
        "ui.json");

    public UiPreferences(ILogger<UiPreferences> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Load();
    }

    /// <summary>界面字号缩放（1.0 / 1.25 / 1.5）。</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>关闭窗口 = 最小化到托盘（关闭则直接退出）。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>开机自启（HKCU Run 键，指向应用目录的 Toolbox.App.exe）。</summary>
    public bool LaunchAtStartup { get; private set; }

    /// <summary>字号缩放变化（主窗口应用 LayoutTransform 并调整窗口尺寸）。</summary>
    public event Action<double>? FontScaleChanged;

    /// <summary>关窗行为变化。</summary>
    public event Action<bool>? CloseToTrayChanged;

    public void SetFontScale(double scale)
    {
        scale = Math.Clamp(scale, MinScale, MaxScale);
        if (Math.Abs(scale - FontScale) < 0.001)
        {
            return;
        }

        FontScale = scale;
        Save();
        FontScaleChanged?.Invoke(scale);
    }

    public void SetCloseToTray(bool value)
    {
        if (CloseToTray == value)
        {
            return;
        }

        CloseToTray = value;
        Save();
        CloseToTrayChanged?.Invoke(value);
    }

    /// <summary>读取/写入 HKCU Run 键实现开机自启（失败只记日志，界面照常显示实际状态）。</summary>
    public bool GetLaunchAtStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            LaunchAtStartup = key?.GetValue("Toolbox") is string;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取开机自启状态失败。");
            LaunchAtStartup = false;
        }

        return LaunchAtStartup;
    }

    public bool SetLaunchAtStartup(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (key is null)
            {
                throw new InvalidOperationException("HKCU\\...\\Run 键不可写。");
            }

            if (enabled)
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "Toolbox.App.exe");
                if (!File.Exists(exe))
                {
                    throw new FileNotFoundException("找不到可执行文件（自启需要固定的 exe 路径）。", exe);
                }

                key.SetValue("Toolbox", $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue("Toolbox", throwOnMissingValue: false);
            }

            LaunchAtStartup = enabled;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "设置开机自启失败（enabled={Enabled}）。", enabled);
            return false;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(_file));
            var root = doc.RootElement;

            if (root.TryGetProperty("fontScale", out var scale) && scale.TryGetDouble(out var s))
            {
                FontScale = Math.Clamp(s, MinScale, MaxScale);
            }

            if (root.TryGetProperty("closeToTray", out var tray) && tray.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                CloseToTray = tray.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取界面偏好失败，使用默认值。");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var json = JsonSerializer.Serialize(new
            {
                fontScale = FontScale,
                closeToTray = CloseToTray,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存界面偏好失败（不影响本次使用）。");
        }
    }
}
