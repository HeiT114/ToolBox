using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Contracts;
using Toolbox.Plugin.SysInfo.Core;

namespace Toolbox.Plugin.SysInfo;

/// <summary>
/// 插件提供的界面：<strong>完全用 C# 代码构建</strong>，不使用 XAML/BAML。
/// </summary>
/// <remarks>
/// <para>
/// 关键约束（都有冒烟测试覆盖，结论见 docs/DESIGN.md「UI 跨 ALC」）：
/// </para>
/// <list type="number">
///   <item><b>本类不派生任何 WPF 类型</b>（不是 <c>UserControl</c>，也不是 <c>Grid</c>）：
///         只要插件里存在"插件定义的 <c>DependencyObject</c> 派生类型"并被实例化，
///         WPF 就会把这个插件 <c>Type</c> 写进进程级的 DP/类型缓存，卸载时 ALC 收不回来。</item>
///   <item><b>挂到视觉树上的根节点是框架类型</b>（这里是 <see cref="Grid"/>）：框架类型的缓存无害。</item>
///   <item><b>不用 BAML</b>：<c>pack://application:,,,/Toolbox.Plugin.SysInfo;component/…</c>
///         的资源程序集解析指向宿主，跨 ALC 必然失败。</item>
/// </list>
/// <para>
/// 卸载纪律：<see cref="Dispose"/> 里把"本视图挂在插件控件上的事件委托"逐个摘掉，
/// 并把子元素清空 —— 视图元素自己管不住被谁引用，但委托链要由插件自己断开。
/// </para>
/// </remarks>
internal sealed class SysInfoView : IPluginViewLifetime, IDisposable
{
    private readonly IPluginLogger _logger;
    private readonly IPluginConfiguration _configuration;
    private readonly Grid _root = new();
    private readonly TextBox _titleInput;
    private readonly TextBox _output;
    private readonly TextBlock _status;
    private readonly List<(Button Button, RoutedEventHandler Handler)> _wired = [];

    public SysInfoView(IPluginConfiguration configuration, IPluginLogger logger)
    {
        _configuration = configuration;
        _logger = logger;
        _root.Margin = new Thickness(14);

        var title = new TextBlock
        {
            Text = "系统环境检测 · 本界面由插件提供（跨 ALC）",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        };

        var hint = new TextBlock
        {
            Text = "点「检测 →」采集当前系统环境；标题来自插件配置（可留空用内置标题）。视图由纯 C# 构建，无 BAML。",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };

        _titleInput = new TextBox
        {
            Text = configuration.Get("title") ?? "系统环境检测",
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 8),
        };
        // 给自动化/无障碍一个稳定的名字，UI 测试才能不靠"猜第几个输入框"来定位
        AutomationProperties.SetName(_titleInput, "报告标题");

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 12,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            Background = Brushes.WhiteSmoke,
        };
        AutomationProperties.SetName(_output, "环境报告");

        _status = new TextBlock
        {
            Text = "就绪 · 点「检测 →」开始采集",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        buttons.Children.Add(CreateButton("检测 →", OnProbe));
        buttons.Children.Add(CreateButton("只看机器信息", OnProbeMachine));
        buttons.Children.Add(CreateButton("清空", OnClear));

        var titleLabel = new TextBlock
        {
            Text = "报告标题",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var header = new StackPanel();
        header.Children.Add(title);
        header.Children.Add(hint);

        for (var i = 0; i < 7; i++)
        {
            _root.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }

        Grid.SetRow(header, 0);
        Grid.SetRow(titleLabel, 1);
        Grid.SetRow(_titleInput, 2);
        Grid.SetRow(buttons, 3);
        Grid.SetRow(_output, 4);
        Grid.SetRow(_status, 5);

        _root.Children.Add(header);
        _root.Children.Add(titleLabel);
        _root.Children.Add(_titleInput);
        _root.Children.Add(buttons);
        _root.Children.Add(_output);
        _root.Children.Add(_status);
    }

    /// <summary>要挂到宿主界面上的框架控件（框架类型，不含插件定义的类型）。</summary>
    public FrameworkElement Root => _root;

    /// <summary><see cref="IPluginViewLifetime.View"/>：交给适配层挂载的根元素。</summary>
    object IPluginViewLifetime.View => _root;

    /// <summary><see cref="IPluginViewLifetime.Release"/>：卸载前的清理回调。</summary>
    void IPluginViewLifetime.Release() => Dispose();

    public void Dispose()
    {
        // 由宿主适配层在卸载前调用（视图已移出视觉树）。
        _logger.LogInformation("SysInfo 视图已释放（宿主适配层调用 Dispose）");

        // ⚠ 卸载纪律：摘掉"插件控件 → 本视图对象"的事件委托。
        foreach (var (button, handler) in _wired)
        {
            button.Click -= handler;
        }

        _wired.Clear();

        // 顺手把子元素摘掉，进一步减少残留引用。
        _root.Children.Clear();
    }

    private Button CreateButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 92,
        };

        button.Click += handler;
        _wired.Add((button, handler));
        return button;
    }

    private void OnProbe(object sender, RoutedEventArgs e) => Render(filter: null);

    private void OnProbeMachine(object sender, RoutedEventArgs e) => Render(filter: "机器");

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _output.Clear();
        _status.Foreground = Brushes.Gray;
        _status.Text = "已清空";
    }

    private void Render(string? filter)
    {
        _output.Text = SystemEnvironmentProbe.Capture().ToReport(_titleInput.Text, filter);
        _status.Foreground = Brushes.SeaGreen;
        _status.Text = $"✔ 检测完成：{_output.Text.Split('\n').Length - 2} 项（filter={filter ?? "<无>"}）";
        _logger.LogInformation(
            $"SysInfo 视图渲染报告：{_output.Text.Length} 字符（filter={filter ?? "<无>"}）");
    }
}
