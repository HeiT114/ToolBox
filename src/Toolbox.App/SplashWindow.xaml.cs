using System.Windows;
using System.Windows.Media.Animation;

namespace Toolbox.App;

/// <summary>
/// 启动画面：主窗口初始化（插件加载）期间显示，主窗口出现后淡出关闭。
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>淡出并关闭（主窗口已显示后调用）。</summary>
    public Task FadeOutAsync()
    {
        var tcs = new TaskCompletionSource();
        var animation = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            Close();
            tcs.TrySetResult();
        };
        BeginAnimation(OpacityProperty, animation);
        return tcs.Task;
    }
}
