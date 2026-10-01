using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.Contracts;
using Toolbox.PluginHost;

namespace Toolbox.Views.Wpf;

/// <summary>
/// 插件视图的宿主控件：负责「中立视图对象 → WPF 视觉树 → 安全摘除」的全部细节。
/// </summary>
/// <remarks>
/// <para>
/// 支持两种返回值形态：
/// <list type="number">
///   <item><see cref="IPluginViewLifetime"/>（推荐）：挂载 <c>View</c>，卸载前调用 <c>Release()</c>；</item>
///   <item>直接是 <see cref="FrameworkElement"/>：直接挂载，若是 <see cref="IDisposable"/> 则卸载前 Dispose。</item>
/// </list>
/// </para>
/// <para>
/// <see cref="Release"/> 是卸载能否回收 ALC 的关键：把视图移出视觉树/逻辑树、清 DataContext 与绑定、
/// 释放焦点与鼠标捕获、回调插件清理，并只留下弱引用供诊断。
/// </para>
/// </remarks>
public sealed class PluginToolViewHost : ContentControl
{
    private IPluginViewLifetime? _pluginView;

    /// <summary>当前承载的插件 Id（<see cref="Release"/> 后为 <see langword="null"/>）。</summary>
    public string? PluginId { get; private set; }

    /// <summary>当前承载的工具 Id（<see cref="Release"/> 后为 <see langword="null"/>）。</summary>
    public string? ToolId { get; private set; }

    /// <summary>最近一次被释放的视图元素（框架类型）的弱引用。</summary>
    public WeakReference? ReleasedViewReference { get; private set; }

    /// <summary>
    /// 最近一次被释放的<strong>插件对象</strong>（<see cref="IPluginViewLifetime"/> 实现）的弱引用。
    /// 卸载后如果它仍然 <c>IsAlive</c>，说明插件对象被别处钉住了（诊断用）。
    /// </summary>
    public WeakReference? ReleasedPluginObjectReference { get; private set; }

    /// <summary>便捷工厂：创建并挂好视图。</summary>
    public static bool TryCreate(
        PluginManager manager,
        string pluginId,
        string toolId,
        out PluginToolViewHost? host,
        out string? error)
    {
        var candidate = new PluginToolViewHost();
        if (candidate.Show(manager, pluginId, toolId, out error))
        {
            host = candidate;
            return true;
        }

        candidate.Release();
        host = null;
        return false;
    }

    /// <summary>
    /// 创建插件视图并挂到本控件上（会先释放上一次的视图）。
    /// </summary>
    /// <returns>成功返回 <see langword="true"/>；失败时 <paramref name="error"/> 给出可读原因。</returns>
    public bool Show(PluginManager manager, string pluginId, string toolId, out string? error)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        Release();

        var result = manager.CreateToolView(pluginId, toolId);
        if (!result.Success || result.View is null)
        {
            error = result.Error ?? "创建视图失败（未提供原因）。";
            return false;
        }

        // 形态 1：视图生命周期包装（推荐）——能拿到"卸载前回调插件"的入口
        IPluginViewLifetime? lifetime = null;
        var root = result.View;
        if (result.View is IPluginViewLifetime wrapper)
        {
            lifetime = wrapper;
            root = wrapper.View;
        }

        // ★ 跨 ALC 类型校验：插件 ALC 与默认 ALC 必须解析到**同一份** WPF 程序集。
        //   插件 ALC 里如果加载了第二份 PresentationFramework，这个 is 会返回 false。
        if (root is not FrameworkElement element)
        {
            error = $"插件返回的视图类型 {root.GetType().FullName} 不是 WPF FrameworkElement"
                + "（通常是插件 ALC 里加载了第二份 WPF 程序集，或视图来自另一个 UI 框架）。";
            return false;
        }

        Content = element;
        _pluginView = lifetime;
        PluginId = pluginId;
        ToolId = toolId;
        error = null;
        return true;
    }

    /// <summary>
    /// 把插件视图从视觉树上摘下来并断掉所有已知引用。幂等，可在卸载前反复调用。
    /// </summary>
    /// <remarks>
    /// 顺序与理由：
    /// <list type="number">
    ///   <item>清焦点 / 鼠标捕获：<c>Keyboard.FocusedElement</c>、<c>Mouse.Captured</c> 是 WPF 的
    ///         全局静态引用，插件控件拿到焦点后会一直挂在那里；</item>
    ///   <item><c>Content = null</c>：从逻辑树 + 视觉树上摘除；</item>
    ///   <item>清 <c>DataContext</c>：断掉绑定表达式对插件对象的引用；</item>
    ///   <item>回调插件的 <see cref="IPluginViewLifetime.Release"/>（退订/停表/关 Popup）；</item>
    ///   <item>释放 <see cref="IDisposable"/>；只保留弱引用用于诊断。</item>
    /// </list>
    /// </remarks>
    public void Release()
    {
        var content = Content;
        var pluginView = _pluginView;
        _pluginView = null;

        if (content is FrameworkElement element)
        {
            if (element is UIElement uiElement)
            {
                // WPF 全局静态引用：不清掉就可能跨插件生命周期持有插件控件
                if (uiElement.IsKeyboardFocusWithin)
                {
                    Keyboard.ClearFocus();
                }

                if (uiElement.IsMouseCaptureWithin)
                {
                    Mouse.Capture(null);
                }
            }

            element.DataContext = null;
            element.ClearValue(FrameworkElement.DataContextProperty);
        }

        // 从逻辑树/视觉树上摘除（ContentControl 会调用 RemoveLogicalChild）
        Content = null;

        // ★ 关键：ContentControl 的模板子节点（ContentPresenter）会**延迟到下一次布局**
        //   才真正摘掉子元素。不补上这一次布局，视图元素仍被 Presenter 间接持有，
        //   于是"视图对象 → 视图类型 → 程序集 → ALC"的链一直不断，回收轮数会明显变多。
        try
        {
            UpdateLayout();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Release 时强制布局失败（可能影响 ALC 回收）：{ex}");
        }

        if (pluginView is not null)
        {
            // 包装形态：Release 就是清理入口，不再重复 Dispose（避免插件被清理两次）
            try
            {
                pluginView.Release();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"调用插件视图 Release 时出错：{ex}");
            }
        }
        else if (content is IDisposable disposable)
        {
            // 直接返回框架元素形态：若是 IDisposable，在这里释放
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                // 插件视图的清理异常不应该影响卸载流程
                System.Diagnostics.Debug.WriteLine($"释放插件视图时出错：{ex}");
            }
        }

        if (pluginView is not null)
        {
            ReleasedPluginObjectReference = new WeakReference(pluginView);
        }

        if (content is not null)
        {
            ReleasedViewReference = new WeakReference(content);
        }

        PluginId = null;
        ToolId = null;
    }
}
