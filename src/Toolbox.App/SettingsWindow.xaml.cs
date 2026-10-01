using System.Windows;

namespace Toolbox.App;

/// <summary>
/// 设置窗口（小齿轮入口）。与主窗口一样，这里刻意不写业务逻辑：
/// DataContext 由 <c>MainViewModel.OpenSettingsCommand</c> 注入只读的 <c>SettingsViewModel</c>。
/// </summary>
/// <remarks>
/// 该窗口不参与插件生命周期，也不持有任何插件对象，因此不会影响 ALC 卸载验证。
/// </remarks>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }
}
