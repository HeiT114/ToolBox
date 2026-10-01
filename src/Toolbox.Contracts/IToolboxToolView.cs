namespace Toolbox.Contracts;

/// <summary>
/// 带界面的工具：视图以<strong>中立返回类型</strong> <see cref="object"/> 交出，
/// 由宿主侧的 UI 适配层转成具体框架的元素（WPF 里就是 <c>FrameworkElement</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么返回 <c>object</c> 而不是 WPF 类型：契约层必须零依赖。一旦这里出现
/// <c>System.Windows.FrameworkElement</c>，所有插件（包括纯命令行插件）都会被绑死到 WPF，
/// 宿主也无法换成 Avalonia / WinUI / 终端 UI。
/// </para>
/// <para>
/// <b>硬性要求</b>：
/// <list type="number">
///   <item>视图必须用<strong>代码构建</strong>，禁止 <c>pack://application:,,,/插件;component/XxxView.xaml</c> 这类 BAML；
///         跨 ALC 时 BAML 的资源程序集解析指向宿主，必然失败（详见 docs/DESIGN.md「UI 跨 ALC」）。</item>
///   <item>视图类型<strong>不要定义自定义 <c>DependencyProperty</c></strong>：WPF 的 DP 全局注册表以
///         <c>ownerType</c> 为键，会把插件类型永久钉在默认 ALC 上，导致卸载回收失败。</item>
///   <item>视图不要把自己注册进 <c>Application.Current.Resources</c>、全局 <c>Style</c>、
///         <c>DispatcherTimer</c>、<c>CompositionTarget.Rendering</c> 等宿主级静态设施；
///         有订阅就在 <c>ShutdownAsync</c> 或视图的 <c>Dispose</c> 里退订。</item>
/// </list>
/// </para>
/// <para>
/// 建议同时实现 <see cref="IToolboxTool"/>：这样同一个工具既能被界面使用，
/// 也能被宿主以 <c>Name + 参数</c> 的方式脚本化调用（本仓库的 Base64Tool 就是这么做的）。
/// 此时 <see cref="Id"/> 应与 <see cref="IToolboxTool.Name"/> 保持一致。
/// </para>
/// </remarks>
public interface IToolboxToolView
{
    /// <summary>工具标识，在插件内唯一。</summary>
    string Id { get; }

    /// <summary>界面上展示的名字（标签页标题）。</summary>
    string DisplayName { get; }

    /// <summary>分类，宿主用来给工具分组（可为空）。</summary>
    string? Category { get; }

    /// <summary>同分类内的展示顺序，越小越靠前。</summary>
    int Order { get; }

    /// <summary>
    /// 创建视图。<paramref name="context"/> 是该插件的 <see cref="IPluginContext"/>，
    /// 视图可以用它取日志与配置。
    /// </summary>
    /// <returns>
    /// 视图对象。WPF 插件请返回 <c>FrameworkElement</c>；宿主适配层拿不到认识的类型时会报错而不是崩溃。
    /// </returns>
    object CreateView(IPluginContext context);
}
