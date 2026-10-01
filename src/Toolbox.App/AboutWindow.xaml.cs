using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace Toolbox.App;

/// <summary>关于窗口：版本 / 运行时 / 架构等静态信息（DataContext = 自身）。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>版本号（InformationalVersion，如 1.0.0-beta）。</summary>
    public string VersionText
    {
        get
        {
            var informational = typeof(AboutWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            var version = string.IsNullOrWhiteSpace(informational)
                ? typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
                : informational;

            var plus = version.IndexOf('+');
            return "v" + (plus > 0 ? version[..plus] : version);
        }
    }

    public string RuntimeText => RuntimeInformation.FrameworkDescription;

    public string OsText => $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}";

    public string ArchText => $"{RuntimeInformation.ProcessArchitecture}（64 位进程）";
}
