# LaLaLand

<img src="src/LaLaLand/Assets/LaLaLand-v2.png" width="96" alt="LaLaLand应用图标">

Windows上的「灵动岛」风格音乐小组件。置顶显示当前歌曲，悬停展开封面、进度和播放控制，进一步展开同步歌词，让音乐信息留在桌面触手可及的位置。

使用C#、.NET 8、WinUI 3和Win2D开发，当前项目版本为0.2.0。媒体信息来自Windows系统媒体会话，默认优先Apple Music，也可跟随系统当前播放器。播放器需要提供系统媒体信息；各播放器的封面、跳转和控制能力可能不同。支持跟随系统当前媒体，也可在设置中锁定 Apple Music、Spotify、网易云音乐、QQ音乐或酷狗音乐。

## 功能概览

- **三种形态**：收起胶囊、悬停展开、完整歌词；收起时可选封面与歌词、纯歌词或封面与控制。
- **桌面外观**：亚克力、毛玻璃、液态玻璃、纯色、封面流光；支持六种停靠位置、多显示器、尺寸与偏移调整。
- **播放控制**：播放/暂停、切歌、拖动进度、点击歌词跳转、播放器音量与静音；Apple Music收藏通过UI Automation实现。
- **同步歌词**：网易云音乐、QQ音乐、LRCLIB和酷狗音乐并发匹配，校验歌名、主唱、录音版本和时长；Spotify等播放器使用通用歌词源匹配；支持逐行、逐字及翻译显示。
- **歌词补救**：导入LRC、增强LRC、YRC或TXT，重新搜索、清除在线缓存、单曲偏移与累计偏差校准。
- **字体与配色**：中英文独立字体、封面渐变或自定义颜色、混色程度与透明度；支持唱片绕排和逐字高亮。
- **托盘与快捷键**：全局控制、开机自启，以及「隐藏面板（仅保留快捷键）」待机；隐藏状态会保存。
- **动态效果与游戏策略**：基于实际音频的频谱、节拍效果；全屏场景可选择性能优化、短暂显示、隐藏或完整效果。
- **媒体故障隔离**：独立媒体进程负责系统媒体监听，异常退出后限次重连，主界面继续运行。

## 快速开始

### 从源码构建

需要Windows开发环境、.NET 8 SDK和可访问NuGet的网络。系统最低目标为Windows 10 2004（19041），推荐Windows 11；当前构建目标使用Windows SDK 22621。以下命令在PowerShell中执行：

```powershell
git clone https://github.com/xqzh268/lalaland.git
cd lalaland
dotnet restore src/LaLaLand/LaLaLand.csproj -p:Platform=x64
dotnet build src/LaLaLand/LaLaLand.csproj -c Debug -p:Platform=x64
& ./src/LaLaLand/bin/x64/Debug/net8.0-windows10.0.22621.0/win-x64/LaLaLand.exe
```

应用启动后驻留托盘。先在播放器中播放一首歌；默认启用「空闲时隐藏」，没有可用媒体会话时可能看不到岛。右键托盘打开设置，`Ctrl+Alt+H`切换显示。再次运行应用会打开已有实例的设置窗口。

### 生成发布目录

```powershell
dotnet publish src/LaLaLand/LaLaLand.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained false -o publish/LaLaLand-win-x64
& ./publish/LaLaLand-win-x64/LaLaLand.exe
```

默认发布包含Windows App SDK，**仍需要目标电脑安装.NET 8 Runtime**（.NET 8 Desktop Runtime也可满足）。分发时保留整个目录，不能只复制EXE。生成含.NET运行时的目录及ARM64构建方法见[开发与发布](docs/development.md)。

### 默认快捷键

| 操作 | 快捷键 |
| --- | --- |
| 播放/暂停 | `Ctrl+Alt+Space` |
| 上一首 / 下一首 | `Ctrl+Alt+←` / `Ctrl+Alt+→` |
| Apple Music收藏 | `Ctrl+Alt+F` |
| 展开/收起歌词 | `Ctrl+Alt+L` |
| 显示/隐藏面板 | `Ctrl+Alt+H` |
| 播放器音量增大 / 减小 | `Ctrl+Alt+↑` / `Ctrl+Alt+↓` |
| 播放器静音 | `Ctrl+Alt+M` |

在设置→快捷键中可自定义或清除绑定。隐藏面板时，播放、切歌、收藏和音量快捷键仍可按需操作播放器；展开歌词快捷键不会恢复隐藏面板。

## 文档导航

| 文档 | 内容 |
| --- | --- |
| [使用指南](docs/usage.md) | 播放源、托盘、歌词导入与校准、外观、游戏策略、数据备份 |
| [开发与发布](docs/development.md) | 环境要求、目录结构、构建、发布、调试工具 |
| [架构说明](docs/architecture.md) | 进程边界、媒体与歌词数据流、渲染、隐藏生命周期 |
| [测试说明](docs/testing.md) | 离线回归、Windows集成测试、联网检查、验证边界 |
| [故障排查](docs/troubleshooting.md) | 不显示、控制失效、缺词、时间偏差、启动与媒体故障 |
| [贡献指南](CONTRIBUTING.md) | 问题报告、变更要求、验证与提交范围 |

## 数据与网络

设置、单曲校准与日志位于`%APPDATA%\LaLaLand`；在线歌词缓存和导入副本位于`%LOCALAPPDATA%\LaLaLand\lyrics`，详见[数据存储](docs/usage.md#数据存储与备份)。在线匹配会向歌词来源发送当前歌曲元数据。音频分析在本机完成，用于频谱与节拍效果。提交问题报告前，请检查日志中的歌曲信息和本机路径。

## 已知限制

- 独占全屏可能绕过桌面合成，覆盖层显示受系统与游戏影响；优先使用无边框窗口。本项目不向游戏进程注入代码。
- 液态玻璃通过背景模糊、高光和着色模拟；不提供真实透镜折射。
- Apple Music收藏依赖播放器界面结构；播放器更新、语言和窗口状态可能影响定位。
- 在线来源的曲库和接口可能变化。逐行歌词的字色推进是估算；来源提供逐字时间轴时才有逐字同步。正确匹配仍可能需要时间校准。
- 项目配置支持x64与ARM64；当前文档验证记录覆盖本机Windows x64，未完成ARM64或全部受支持Windows版本的运行验收。
- 构建或自动化检查通过不代表所有播放器兼容，也不代表歌词与实际声音已完成听感对齐。

## 许可证

项目沿用仓库的GNU Affero General Public License v3.0，全文见[LICENSE](LICENSE)。
