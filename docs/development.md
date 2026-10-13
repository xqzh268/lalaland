# 开发与发布

[返回README](../README.md) · [架构说明](architecture.md) · [测试说明](testing.md)

## 环境与依赖

| 项目 | 当前配置 |
| --- | --- |
| 开发系统 | Windows；完整UI构建与运行需要Windows环境 |
| .NET | .NET 8 SDK，目标`net8.0-windows10.0.22621.0` |
| 最低系统目标 | Windows 10 2004，`10.0.19041.0` |
| Windows SDK引用 | 由目标框架解析；项目指定Windows SDK包版本`10.0.22621.57` |
| Windows App SDK | `Microsoft.WindowsAppSDK` 1.8.260921001 |
| 绘制 | `Microsoft.Graphics.Win2D` 1.4.0 |
| UI Automation | `Interop.UIAutomationClient` 10.19041.0 |
| 架构 | x64、ARM64；未指定或AnyCPU时默认为x64 |
| 打包 | 非MSIX应用，`WindowsPackageType=None` |

依赖以[src/LaLaLand/LaLaLand.csproj](../src/LaLaLand/LaLaLand.csproj)为准。首次构建需要NuGet还原；不需要配置歌词API密钥。Visual Studio可打开`LaLaLand.sln`，安装支持WinUI/C#开发的组件；命令行构建使用`dotnet`。

```powershell
dotnet --list-sdks
dotnet --list-runtimes
```

确认有8.x SDK。默认发布运行所需的框架是`Microsoft.NETCore.App 8.x`；安装.NET 8 Runtime、.NET 8 Desktop Runtime或相应SDK均可提供它。Windows App SDK自包含与.NET自包含是两个不同开关。

## 目录结构

```text
LaLaLand.sln                         WinUI应用解决方案
src/LaLaLand/
  Program.cs                        主入口、媒体子进程入口、崩溃监护
  App.xaml.cs                       应用、托盘、快捷键与窗口生命周期
  App.*Acceptance.cs                Debug下的Windows集成验证
  Views/                            岛窗口、设置窗口及功能分部
  Controls/                         歌词渲染、排版、过渡、进度控件
  Services/                         媒体、歌词、音频、封面、设置等服务
  Models/                           设置、曲目信息、校准与字体脚本
  Interop/                          原生窗口、背景合成、消息循环
  Assets/                           应用图标
tests/LaLaLand.Lyrics.Tests/         直接编译部分生产代码的控制台回归
tools/                              PowerShell验证与调试工具
docs/                               使用、开发、架构、测试与排查说明
publish/                            本机发布目录，Git忽略
output/                             本机验证报告与调试产物，Git忽略
```

解决方案只包含应用项目；歌词控制台测试需单独运行，不能仅以解决方案构建判断测试结果。

## 构建与启动

以下命令均从仓库根目录执行。

```powershell
dotnet restore src/LaLaLand/LaLaLand.csproj -p:Platform=x64
dotnet build src/LaLaLand/LaLaLand.csproj -c Debug -p:Platform=x64
& ./src/LaLaLand/bin/x64/Debug/net8.0-windows10.0.22621.0/win-x64/LaLaLand.exe
```

Release构建：

```powershell
dotnet build src/LaLaLand/LaLaLand.csproj -c Release -p:Platform=x64
```

如已有正式实例运行，普通启动只会激活其设置。集成测试使用独立实例标识和数据目录，详见[测试说明](testing.md)。

## 发布

### 默认x64发布

```powershell
dotnet publish src/LaLaLand/LaLaLand.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained false -o publish/LaLaLand-win-x64
```

项目默认`WindowsAppSDKSelfContained=true`、`SelfContained=false`，所以目录包含Windows App SDK，但目标电脑仍需.NET 8 Runtime。保留全部DLL、`Assets`、PRI及配置文件；单独拷贝EXE不能构成可运行分发。

### 包含.NET运行时

```powershell
dotnet publish src/LaLaLand/LaLaLand.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish/LaLaLand-win-x64-self-contained
```

该命令包含.NET运行时，产物更大，仍以整个目录分发。这里不启用单文件发布或裁剪，避免改变现有WinUI资源部署方式。分发前将仓库`LICENSE`放入发布目录，并提供本仓库源码入口。

### ARM64

```powershell
dotnet publish src/LaLaLand/LaLaLand.csproj -c Release -p:Platform=ARM64 -r win-arm64 --self-contained false -o publish/LaLaLand-win-arm64
```

这是项目已配置的目标，当前文档验证不包含ARM64；需在实际ARM64设备验证窗口、原生依赖、媒体与音频捕获后再宣称支持。

发布验收先检查目录完整性，再在目标环境实际启动、退出、读取媒体和操作设置。编译成功不等于运行验收。升级前从托盘退出正式实例，备份旧发布目录和用户配置，再替换完整发布目录，避免新旧DLL与PRI混用。

## 调试工具

| 工具 | 用途 |
| --- | --- |
| `tools/test-*.ps1` | 歌词回归、窗口生命周期、外观、布局、媒体验证，参数见[测试说明](testing.md) |
| `tools/dump-uia.ps1` | 导出正在运行的Apple Music窗口UI Automation树 |
| `tools/screenshot.ps1` | 截取主屏指定区域；坐标使用物理像素 |
| `tools/capture-window.ps1` | 按标题通过`PrintWindow`捕获窗口 |
| `tools/mouse.ps1` | 移动鼠标，可用`-Click`点击指定物理坐标 |
| `tools/make-icon-v2.ps1` | 将当前PNG图标生成Windows ICO |
| `tools/make-icon.ps1` | 旧版绘制图标工具，当前项目引用v2图标 |

运行UI树、截图或鼠标工具时先核对目标窗口；输出写到`output/`，分享前去除本机信息。`--media-worker`与`--watchdog <pid>`是内部进程入口，不是日常启动参数。

提交前运行与变更相关的[验证](testing.md)，检查`git diff --check`，并确认没有提交`bin/`、`obj/`、`publish/`、`output/`或实际歌词与用户配置。
