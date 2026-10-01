using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toolbox.App.ViewModels;
// WinForms 被隐式 using（托盘用）：这些名字与 WPF 同名，显式取 WPF 的。
using Application = System.Windows.Application;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;

namespace Toolbox.App;

/// <summary>
/// 主窗口：只放"视图层"的逻辑 —— 键盘（Ctrl+K 命令面板）、无边框窗口的窗口按钮与
/// 最大化内缩、侧栏折叠动画、托盘图标、关闭到托盘。业务全部在 <see cref="MainViewModel"/>。
/// </summary>
public partial class MainWindow : Window
{
    private const double RailExpandedWidth = 168;
    private const double RailCollapsedWidth = 62;

    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _forceClose;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ── 自定义标题栏：窗口按钮 ─────────────────────────────────────────────

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>关闭按钮 = 最小化到托盘（真正退出走托盘菜单或 Ctrl+K「退出程序」）。</summary>
    private void OnCaptionCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 最大化时给 RootGrid 7px 内缩：无边框窗口的最大化几何带不可见边框，
    /// 不内缩会让内容顶出屏幕/压到任务栏。
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (RootGrid is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        RootGrid.Margin = maximized ? new Thickness(7) : new Thickness(0);

        if (MaxGlyph is not null)
        {
            MaxGlyph.Text = maximized ? "\uE923" : "\uE922";
        }
    }

    // ── 侧栏折叠（图文 → 纯图标）──────────────────────────────────────────

    /// <summary>
    /// 折叠动画 / 字形 / UIA 名称都挂到 <see cref="MainViewModel.IsRailExpanded"/> 的属性变化上：
    /// UIA 的 TogglePattern 不触发 Click 事件，绑定值的变化才是唯一可靠的信号源。
    /// </summary>
    private void UpdateRailVisuals()
    {
        var vm = Vm;
        if (vm is null || RailCard is null)
        {
            return;
        }

        var target = vm.IsRailExpanded ? RailExpandedWidth : RailCollapsedWidth;
        var animation = new DoubleAnimation(RailCard.Width, target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        RailCard.BeginAnimation(WidthProperty, animation);

        if (RailCollapseGlyph is not null)
        {
            RailCollapseGlyph.Text = vm.IsRailExpanded ? "\uE76B" : "\uE76C";
        }

        if (RailToggle is not null)
        {
            AutomationProperties.SetName(RailToggle, vm.IsRailExpanded ? "收起侧栏" : "展开侧栏");
        }
    }

    // ── Ctrl+K 命令面板 ────────────────────────────────────────────────────

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
        {
            return;
        }

        // Ctrl+K：任何地方呼出/收起（隧道事件先于插件视图收到，插件无需感知）
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            vm.TogglePalette();
            e.Handled = true;
            return;
        }

        if (!vm.IsPaletteOpen)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                vm.IsPaletteOpen = false;
                e.Handled = true;
                break;
            case Key.Down:
                vm.MovePaletteSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MovePaletteSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = vm.RunPaletteSelectedAsync();
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.IsPaletteOpen = false;
        }
    }

    /// <summary>面板打开时把键盘焦点放进搜索框。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is MainViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;

            // 持久化的字号缩放在窗口创建时就要应用（属性变化事件只在切换时触发）
            ApplyFontScale(vm.FontScale);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRailExpanded))
        {
            UpdateRailVisuals();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.FontScale))
        {
            ApplyFontScale(Vm?.FontScale ?? 1.0);
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.IsPaletteOpen) || Vm is not { IsPaletteOpen: true })
        {
            return;
        }

        PaletteSearchBox.Focus();
        PaletteSearchBox.SelectAll();
        PlayPaletteOpenAnimation();
    }

    /// <summary>
    /// 字号缩放：整个 RootGrid 用 LayoutTransform 缩放（布局/命中同步跟随），
    /// 窗口同步放大但不超过工作区；正文区有 ScrollViewer 兜底，超了就滚动不裁切。
    /// </summary>
    private void ApplyFontScale(double scale)
    {
        RootGrid.LayoutTransform = new ScaleTransform(scale, scale);

        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        Width = Math.Clamp(1440 * scale, 1080, area.Width * 0.98);
        Height = Math.Clamp(900 * scale, 640, area.Height * 0.98);
    }

    /// <summary>
    /// 面板入场动画：遮罩淡入 + 卡片淡入 + 轻微缩放/下落。
    /// Style.Triggers 里不允许 TargetName，所以用代码起 Storyboard；
    /// 动画只作用在面板自身（宿主元素），与插件视图无关。
    /// </summary>
    private void PlayPaletteOpenAnimation()
    {
        try
        {
            var scale = FindName("PaletteScale") as ScaleTransform;
            var slide = FindName("PaletteSlide") as TranslateTransform;

            var storyboard = new Storyboard();

            void Add(DependencyObject? target, DependencyProperty property, double from, double to, double milliseconds)
            {
                if (target is null)
                {
                    return;
                }

                var animation = new DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(milliseconds),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, target);
                Storyboard.SetTargetProperty(animation, new PropertyPath(property));
                storyboard.Children.Add(animation);
            }

            Add(PaletteDim, UIElement.OpacityProperty, 0, 0.45, 180);
            Add(PaletteCard, UIElement.OpacityProperty, 0, 1, 200);
            Add(scale, ScaleTransform.ScaleXProperty, 0.96, 1, 220);
            Add(scale, ScaleTransform.ScaleYProperty, 0.96, 1, 220);
            Add(slide, TranslateTransform.YProperty, 12, 0, 220);

            if (storyboard.Children.Count > 0)
            {
                storyboard.Begin();
            }
        }
        catch (Exception)
        {
            // 动效失败不影响功能
        }
    }

    // ── 插件右键菜单 ──────────────────────────────────────────────────────

    /// <summary>右键落在哪一行，就先把哪一行选中（菜单作用于"被右键的插件"）。</summary>
    private void OnPluginListPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is not null && !item.IsSelected)
        {
            item.IsSelected = true;
        }
    }

    // ── 托盘 + 关闭到托盘 ──────────────────────────────────────────────────

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_tray is not null)
        {
            return;
        }

        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开主窗口", null, (_, _) => RestoreFromTray());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ForceClose());

            System.Drawing.Icon? icon = null;
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                icon = new System.Drawing.Icon(stream, 48, 48);
            }

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "Toolbox · 插件化工具箱",
                Visible = true,
                ContextMenuStrip = menu,
                Icon = icon ?? System.Drawing.SystemIcons.Application,
            };
            _tray.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch (Exception)
        {
            // 托盘是锦上添花：失败不影响主功能（也绝不影响卸载验证）
            _tray = null;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
        {
            return;
        }

        // 设置里可以关掉"关窗到托盘"：关掉后点关闭 = 真正退出（走容器 Dispose 卸载流程）
        if (Vm is { } vm && !vm.CloseToTray)
        {
            return;
        }

        // 默认行为：关窗 = 最小化到托盘（真退出走托盘菜单或命令面板「退出程序」）
        e.Cancel = true;
        Hide();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloonTip(
                3000,
                "Toolbox",
                "已最小化到托盘。右键托盘图标可退出。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>真正的退出（会触发 Application 关闭 → 容器 DisposeAsync → 卸载全部插件）。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        if (Vm is { } vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnClosed(e);
    }
}
