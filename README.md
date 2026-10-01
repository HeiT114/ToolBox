# Toolbox · 插件化工具箱骨架（.NET 10 / C# 14）

**当前版本：1.0.0-beta**

一个**可运行**的插件化工具箱骨架：主程序负责 UI / 插件管理 / 配置 / 日志；
契约层零依赖；插件宿主用 `AssemblyLoadContext` + `AssemblyDependencyResolver`
从 `plugins/` 动态加载 **dll / 文件夹 / zip**；DI 用 `Microsoft.Extensions.DependencyInjection`。

> 详细设计（加载时序、PluginLoadContext 逐条说明、DI 边界、**卸载注意事项清单**）见
> [docs/DESIGN.md](docs/DESIGN.md)。
> 图文介绍页 + 教程（含截图、快捷键、插件开发三步）见 [docs/intro.html](docs/intro.html)（浏览器直接打开）。

---

## 快速开始

```powershell
# 构建 + stage 示例插件 + 跑端到端冒烟测试（推荐先跑这个）
pwsh ./scripts/verify.ps1

# 只看详细日志
pwsh ./scripts/verify.ps1 -VerboseLogs

# 顺便启动 WPF 主程序（示例插件已经放进主程序输出目录的 plugins/）
pwsh ./scripts/verify.ps1 -RunApp
```

前提：**.NET 10 SDK**。脚本会自己找：先看 `PATH` 上的 `dotnet`，再看
`%USERPROFILE%\.dotnet10\dotnet.exe`，都不行就用 `-Dotnet` 指定：

```powershell
pwsh ./scripts/verify.ps1 -Dotnet 'C:\Users\me\.dotnet10\dotnet.exe'
```

手工命令等价于：

```powershell
dotnet build Toolbox.slnx -c Release
dotnet artifacts/bin/Toolbox.PluginHost.SmokeTest/release/Toolbox.PluginHost.SmokeTest.dll --plugins artifacts/plugins --verbose
dotnet artifacts/bin/Toolbox.App/release/Toolbox.App.dll          # 启动主程序
```

---

## 目录结构

```
Toolbox.slnx                      # .slnx 解决方案（.NET 10）
Directory.Build.props             # net10/C#14、Nullable、警告即错误、artifacts 输出布局
Directory.Packages.props          # 中央包管理（Microsoft.Extensions.* = 10.0.12）
src/Toolbox.Contracts/            # 契约层：零依赖（IToolboxPlugin / IToolboxTool / IPluginContext…）
src/Toolbox.PluginHost/           # 宿主：PluginLoadContext / PluginLoader / PluginManager / zip 影子解包
src/Toolbox.App/                  # 主程序：WPF UI（Themes/ModernTheme.xaml）+ 组合根(DI) + appsettings.json
plugins/Toolbox.Plugin.SysInfo/   # 初始测试插件：检测当前系统环境（带跨 ALC 的报告界面）
plugins/Toolbox.Plugin.SysInfo.Core # 该插件的私有依赖（验证 AssemblyDependencyResolver 隔离）
tests/Toolbox.PluginHost.SmokeTest# 端到端冒烟测试（控制台，非 xunit，退出码即结果）
scripts/verify.ps1                # 构建 → stage 插件（目录 + zip 两种形态）→ 跑宿主机冒烟测试
scripts/ui-smoke.ps1              # GUI 端到端：真实检测一次系统环境 → 右键卸载 → 校验 ALC 回收
scripts/ui-redesign-smoke.ps1     # GUI 功能验证：左栏 / 右键菜单 / Ctrl+K 面板 / 结果历史 / 主题
scripts/pack.ps1                  # 发布自包含 exe + zip（artifacts/dist）
scripts/make-assets.ps1           # 纯代码生成应用图标（app.ico，M4 立方体样式，多尺寸 PNG 帧）
scripts/icon-variants.ps1         # 图标候选变体对照大图生成（挑选样式用）
scripts/capture.ps1               # PrintWindow 抓截图（浅色/深色/命令面板/设置）→ artifacts/ui-review
scripts/make-installer.ps1        # Inno Setup 编译安装包 → artifacts/dist/Toolbox-Setup-<版本>.exe
installer/toolbox.iss             # 安装包脚本（简体中文向导、按用户安装、卸载清理）
artifacts/                        # 所有构建输出（bin/plugins/dist/…），已 gitignore
```

---

## 主程序长什么样

主窗口是一个自绘的插件管理台（`src/Toolbox.App/Themes/ModernTheme.xaml` 提供全部外观）：

```
┌──────────────────────────────────────────────────────────────────────┐
│ (头像) Toolbox … · v1.0.0-beta   [搜索 Ctrl+K]          ─  □  ✕      │ ← 无边框自定义标题栏(56px)
├──────────────┬───────────────┬───────────────────────┬───────────────┤
│ 图文工具栏    │ 插件列表/详情 │ 插件界面 ⇄ 日志        │ 结果历史       │
│ 可折叠文字    │ 右键=单插件   │ ← 插件自己构建的视图   │    右侧        │
│ ⚙设置/ⓘ关于  │               │                       │               │
│（左下角）     │               │                       │               │
├──────────────┴───────────────┴───────────────────────┴───────────────┤
│ 状态栏：已加载插件：N / M · 视图标签页：K · <最近操作>                │
└──────────────────────────────────────────────────────────────────────┘
（另有：启动画面 splash · Ctrl+K 命令面板浮层 · 插件市场窗口 · 关闭窗口最小化到托盘）
```

* **启动画面**：启动先出深色 splash（图标 + 进度条动画），插件加载完成后与主窗口淡入交接；
* **无边框窗口**：`WindowChrome` 自绘标题栏 —— 顶部空白区可拖拽/双击最大化，
  右上角 ─ □ ✕ 自定义窗口按钮（关闭 = 最小化到托盘），最大化时内容自动内缩 7px 不压任务栏；
* **左上角**：软件图标（圆形裁剪 + 悬停放大/选中光环，不再显示用户首字母，点击展开用户信息卡）；
  顶部搜索胶囊 = Ctrl+K 命令面板入口；
* **最左侧图文工具栏**（默认带文字，顶部开关可折叠成纯图标，动画宽度过渡）：
  * **日志**（独立功能：与中间的插件界面互斥切换，控件始终留在视觉树上 → 不影响卸载验证）；
  * **添加工具**（zip / dll / 文件夹 → 装进 plugins/ 并立即加载）、**插件市场**（带「敬请期待」徽标）、**打开插件目录**；
  * **左下角**：**关于**（版本/运行时/说明）与 **设置**；
* **设置窗口：分类放左、内容放右**（导航：外观 / 目录 / 插件宿主 / 启动与托盘 / 配置覆盖 / 配置文件，切换带淡入）：
  * 外观：浅色 / **深色（带「内测」小标签）** + **字号缩放 100/125/150%**（主窗口同步放大，超屏正文区出滚动条）；
  * 目录：四条路径 + **重新扫描插件目录 / 重新加载全部插件**（从原工具栏移入）+ 一键打开各目录；
  * 启动与托盘：**关窗最小化到托盘开关**（关掉=点关闭直接退出）、**开机自启开关**（HKCU Run，免管理员）；
* **插件列表**：顶部**过滤框**（按名称/Id 实时筛选可见行）；
  **单击插件 = 直接打开**（未加载则加载，并把它的界面标签页切到前台）；
  **右键插件 = 单插件菜单**：打开 / 卸载（验证 ALC 回收）/ 重载（新 ALC）/ 删除插件（带确认，从磁盘移除）；
* **Ctrl+K 命令面板**：搜插件 / 执行宿主命令 / 运行工具 / 打开市场·设置·关于 / 切主题 / 退出；
  ↑↓ 选择、Enter 执行、Esc 关闭；
* **插件市场**（内测 · 敬请期待徽标）：浏览本地 `market/` 目录（与 plugins/ 分离，不会被自动加载），
  空市场自动从已安装的目录插件播种一个 zip（id 加 `-market` 后缀避免撞车），一键安装即加载，
  也支持「从文件安装」；
* **右**：**结果历史**（每次执行/加载/卸载/删除留档，单条/整表**复制**、**导出 txt**、清空）；
* **主题**：深色 / 浅色一键切换（设置窗口或 Ctrl+K），暗色整体提过对比度（次级/弱化文字、
  悬停层、禁用态、菜单模板都按暗色重调）；颜色全部走 `DynamicResource` + 调色板整体换色，
  记住在 `%LOCALAPPDATA%\Toolbox\theme.txt`；启动参数 `--theme dark|light` 可覆盖（不写回）；
* **托盘**：关闭窗口默认最小化到托盘（气泡提示一次），托盘菜单可还原窗口或退出（退出才真正卸载全部插件）；
* **动效**：启动画面进度条循环、窗口入场淡入上移、工具栏悬停/折叠宽度过渡、齿轮悬停旋转、
  列表项底色/强调条渐变、标签页下划线生长、日志与历史逐行淡入、命令面板淡入缩放、
  窗口按钮/菜单项悬停色层、主题切换即时换肤。

UI **只绑定字符串投影**（`PluginDescriptor`），不缓存任何插件对象——这是卸载能生效的前提。
同理，主题里**不给插件视图（或它的任何祖先容器）挂动画**：动画计时器会持有目标元素，
等于把插件对象钉在计时器上，卸载回收验证必然失败。动画只做宿主自己的外观元素
（日志视图切换也只是 `Visibility=Collapsed`，控件不移出视觉树）。

键盘/自动化可访问性：工具栏按钮、头像、窗口按钮、插件列表行、菜单项都带 `AutomationProperties.Name`，
插件界面里的关键控件也由插件自己标注（`报告标题` / `环境报告`），GUI 测试因此不依赖"猜第几个控件"。

---

## 写一个插件（3 步）

```powershell
dotnet new classlib -n Acme.Toolbox.MyPlugin -o plugins/Acme.Toolbox.MyPlugin
```

```xml
<!-- plugins/Acme.Toolbox.MyPlugin/Acme.Toolbox.MyPlugin.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- 关键：契约由宿主提供，绝不复制进插件目录 -->
    <ProjectReference Include="..\..\src\Toolbox.Contracts\Toolbox.Contracts.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

```csharp
using Toolbox.Contracts;

[ToolboxPlugin("acme.myplugin", DisplayName = "我的插件", Version = "1.0.0")]
public sealed class MyPlugin : IToolboxPlugin
{
    private IPluginContext? _context;

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context;
        context.Logger.LogInformation($"初始化完成，数据目录 {context.DataDirectory}");
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default)
    {
        _context = null;                 // 卸载纪律：自己也要把引用清干净
        return ValueTask.CompletedTask;
    }
}

public sealed class UpperTool(IPluginLogger logger) : IToolboxTool   // 构造注入来自插件私有容器
{
    public string Name => "upper";
    public string? Description => "转大写";

    public ValueTask<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, string?> args, CancellationToken ct = default)
    {
        args.TryGetValue("text", out var text);
        logger.LogInformation($"upper({text})");
        return ValueTask.FromResult(ToolResult.Ok(text?.ToUpperInvariant() ?? string.Empty));
    }
}
```

发布：把 `dotnet publish`（或 build）的输出目录放进 `plugins/<名字>/` 即为**目录插件**；
把输出目录里的内容打成 zip 放进 `plugins/<名字>-<版本>.zip` 即为 **zip 插件**。
zip 里只要不止一个 dll，就必须带 `plugin.json`（`entryAssembly` 指定入口）。

---

## 验证结果（本机实测）

| 项 | 结果 |
| --- | --- |
| SDK / 运行时 | .NET SDK `10.0.401`，运行时 `10.0.12`（Windows Desktop 亦为 10.0.12） |
| 全量构建 | `dotnet build Toolbox.slnx -c Release` → **0 警告 0 错误**（`TreatWarningsAsErrors=true`） |
| 宿主机冒烟测试 | `scripts/verify.ps1` → **80 项断言全部通过**（退出码 0） |
| 目录插件 | 加载 / 工具调用 / 契约共享（插件 ALC 内**不含** `Toolbox.Contracts`） |
| zip 插件 | 影子解包、`AssemblyDependencyResolver` 解析私有依赖、私有依赖**未泄漏**到默认 ALC |
| 配置 | `plugin.json` 默认值 < 主程序 `PluginHost:Plugins:<id>` 覆盖（重载后生效） |
| 卸载 | **1 轮 GC** 即回收（`WeakReference.IsAlive == false`），回收后 zip 影子目录可立即删除（文件锁释放） |
| 重载 | 卸载并确认回收后可再次加载同一路径（新 ALC），zip 解包幂等复用同一影子目录 |
| 失败路径 | 入口程序集缺失 → `PluginLoadException` + 描述进入 `Failed` 状态带原因 |
| 跨 ALC UI | 视图构建器类型来自插件程序集、内层 `View` 是宿主侧 `FrameworkElement`；**视图仍挂在视觉树上时回收必定失败**（这条反向断言证明"卸载前必须先摘视图"确实是必要条件） |
| 主程序 GUI（卸载） | `scripts/ui-smoke.ps1` → **20 / 20 通过**：在插件界面上真实点「检测 →」，把报告里的机器名/用户名/逻辑处理器/系统目录与 PowerShell 侧独立取到的值逐项比对；再走**右键菜单卸载** → 标签页消失、报告框消失、结果历史留档、日志 `插件 sysinfo 已卸载并回收（1 轮 GC …）`、无「未被回收」告警 |
| 主程序 GUI（功能） | `scripts/ui-redesign-smoke.ps1` → **82 / 82 通过**：启动画面先于主窗口、图文工具栏 6 按钮齐全且在左侧 12% 内、折叠（文字隐藏 + 栏体 154→48px + 开关改名）、自定义标题栏三窗口按钮、添加工具浮层、Ctrl+K 面板（呼出/过滤/收起）、插件过滤框（清空/恢复）、结果历史（复制/清空/导出入口）、日志独立视图往返切换、设置**左分类右内容**导航（外观含深色「内测」标签与字号 100→125→100 缩放往返、目录含重新扫描/重新加载、插件宿主、启动与托盘两个开关）、关于窗口（版本/运行时）、插件市场（「敬请期待」徽标 + 播种安装列表 + 从文件安装 + 关闭）、头像左上角、工具输出在右侧、插件标签页/列表行/状态栏等 ui-smoke 观测点全部保留 |
| 打包 | `scripts/pack.ps1` → 自包含 exe 目录 + zip（`artifacts/dist`，约 172 MB / zip 71 MB） |
| 安装包 | `scripts/make-installer.ps1`（Inno Setup 6，简体中文向导）→ `artifacts/dist/Toolbox-Setup-1.0.0-beta.exe`（约 51 MB）；实测静默安装 → 启动 → 静默卸载全通过（目录、开始菜单快捷方式都被清掉） |

复现：

```powershell
pwsh ./scripts/verify.ps1              # 宿主机 80 项断言
pwsh ./scripts/ui-smoke.ps1            # GUI 20 项（含真实环境检测 + 右键卸载回收）
pwsh ./scripts/ui-redesign-smoke.ps1   # GUI 82 项（标题栏/折叠/日志切换/设置分类导航/字号/开关/市场/关于）
pwsh ./scripts/make-assets.ps1         # 重新生成应用图标（app.ico）
pwsh ./scripts/capture.ps1             # 重抓 4 张截图（浅色/命令面板/设置/深色）
pwsh ./scripts/pack.ps1                # 自包含 exe + zip
pwsh ./scripts/make-installer.ps1      # 编译安装包（需 Inno Setup 6：winget install JRSoftware.InnoSetup）
```

> 跑 GUI 脚本前请先关掉正在运行的主程序：插件 dll 被占用时 `verify.ps1` 无法清空插件目录，
> 脚本现在会直接报错（早期版本会静默残留旧插件，导致 UI 验证连到旧插件上，断言全部失效）。

---

## 已知边界

* 插件是**完全信任**代码：`AssemblyLoadContext` 不是安全沙箱，权限隔离要另起进程。
* 宿主不能以 `PublishTrimmed` / NativeAOT / 单文件方式发布（会破坏反射发现与动态加载）。
* 卸载是协作式弱保证：附加调试器/诊断工具时回收可能失败，详见
  [DESIGN.md §7.2](docs/DESIGN.md)。
* **任何仍然活着的外部引用都会钉住插件 ALC**，包括 UI 自动化客户端（UIA）持有的插件界面元素引用。
  所以 `scripts/ui-smoke.ps1` 在卸载前会显式释放自己拿到的插件元素引用并触发一次 GC——
  这不是"给测试放水"，而是"客户端别再拿着插件对象"这一条纪律的实例。
* `PluginManager` 按 **插件 Id** 管理描述与句柄：同一个 Id 的目录形态与 zip 形态**不要同时**放进 `plugins/`，
  否则后者会被当成热替换顶掉前者，描述状态与界面行都会看起来重复
  （`scripts/verify.ps1` 因此给包里的清单换了一个 Id 来同时覆盖两种形态）。
