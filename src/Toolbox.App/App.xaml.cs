using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Toolbox.App.Services;
using Toolbox.App.ViewModels;
using Toolbox.PluginHost;
using AppMainWindow = Toolbox.App.MainWindow;
// WinForms 被隐式 using（托盘用），它的 Application / MessageBox 与 WPF 同名：显式取 WPF 的。
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Toolbox.App;

/// <summary>
/// 主程序：负责 UI、插件管理、配置与日志（插件只提供"工具"）。
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _services = CompositionRoot.Build();

            // 主题必须在主窗口创建前就位：窗口的 DynamicResource 首次解析发生在 Show() 时。
            // 支持 --theme light|dark 覆盖上次保存的选择。
            _services.GetRequiredService<ThemeService>().Initialize(e.Args);

            var viewModel = _services.GetRequiredService<MainViewModel>();
            var window = _services.GetRequiredService<AppMainWindow>();
            window.DataContext = viewModel;

            // 启动画面：先出 splash，再在它后面完成插件加载，主窗口就绪后淡出交接。
            var splash = new SplashWindow();
            splash.Show();

            // 注意：这里必须用 this.MainWindow（Application 的属性），
            // 直接写 MainWindow 会与同名类型冲突。
            this.MainWindow = window;

            await viewModel.InitializeAsync();

            window.Show();
            window.Activate();

            // 让主窗口先渲染出首帧，再淡出启动画面（交接不闪屏）
            await Task.Delay(350);
            await splash.FadeOutAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}\n\n{ex}", "Toolbox", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            // DisposeAsync 会级联释放 PluginManager（卸载全部插件并回收 ALC）与日志 Provider。
            try
            {
                await _services.DisposeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"释放容器失败：{ex}");
            }

            _services = null;
        }

        base.OnExit(e);
    }
}

/// <summary>
/// 组合根：整个程序唯一"知道所有实现"的地方。
/// </summary>
/// <remarks>
/// 分层约定：
/// <list type="bullet">
///   <item>契约层零依赖；</item>
///   <item>PluginHost 依赖契约 + DI/日志<em>抽象</em>；</item>
///   <item>只有主程序引用 DI/日志/配置的<strong>实现包</strong>，并在这里装配。</item>
/// </list>
/// </remarks>
internal static class CompositionRoot
{
    public static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var sink = new ObservableLogSink();
        var dispatcher = Application.Current.Dispatcher;

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(sink);

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(ParseLevel(configuration["Logging:LogLevel:Default"]) ?? LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs")));
            builder.AddProvider(new ObservableLogProvider(sink, dispatcher));
        });

        // ⚠ 顺序有意义：AddToolboxPluginHost 内部用 TryAdd 注册默认的 NullPluginSettingsProvider，
        //   先注册真实实现，默认实现就不会生效。
        services.AddSingleton<IPluginSettingsProvider, ConfigurationPluginSettingsProvider>();

        // 插件宿主：路径、共享程序集、卸载验证都来自 appsettings.json 的 PluginHost 节点
        services.AddToolboxPluginHost(options =>
        {
            configuration.GetSection("PluginHost").Bind(options);

            // ⚠ 配置绑定器会把配置文件里的数组"追加"到 Options 已有的默认值后面，
            //    于是默认的 ["Toolbox.Contracts"] 会变成两条。这里做一次归一化，
            //    避免共享清单 / 禁用清单里出现重复项（设置窗口会直接把它显示出来）。
            options.SharedAssemblies = [.. options.SharedAssemblies.Distinct(StringComparer.OrdinalIgnoreCase)];
            options.DisabledPlugins = [.. options.DisabledPlugins.Distinct(StringComparer.OrdinalIgnoreCase)];
        });

        services.AddSingleton<ThemeService>();
        services.AddSingleton<UiPreferences>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<AppMainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static LogLevel? ParseLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : null;
}
