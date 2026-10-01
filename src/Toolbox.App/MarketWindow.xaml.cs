using System.Windows;

namespace Toolbox.App;

/// <summary>插件市场窗口（DataContext = <see cref="ViewModels.MarketViewModel"/>，由主视图模型创建）。</summary>
public partial class MarketWindow : Window
{
    public MarketWindow()
    {
        InitializeComponent();
    }
}
