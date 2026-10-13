# 测试说明

[返回README](../README.md) · [开发与发布](development.md)

以下命令在仓库根目录的Windows PowerShell中运行。完整应用需Windows交互式桌面；控制台歌词回归不启动WinUI。集成脚本内部构建使用`--no-restore`，全新克隆先还原：

```powershell
dotnet restore src/LaLaLand/LaLaLand.csproj -p:Platform=x64
```

## 离线歌词回归

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-lyrics.ps1
```

测试项目是控制台执行器，不是`dotnet test`测试框架；解决方案也未包含它。脚本运行下列命令并检查退出码，将报告保存到`output/lyrics-acceptance/<时间戳>/`：

```powershell
dotnet run --project tests/LaLaLand.Lyrics.Tests/LaLaLand.Lyrics.Tests.csproj -c Release
```

直接链接生产解析、匹配、校准、字体脚本和布局代码，用HTTP桩验证多源行为。覆盖偏移、重复副歌、双语时间戳、错歌手/错版本拒绝、时长身份、多源回退、取消与临时失败重试、本地覆盖、校准逆变换、倍速延时、绕排顺序及字体分段。测试缓存使用独立临时目录。

## Windows集成检查

这些脚本构建Debug应用，通过`LALALAND_PANEL_ACCEPTANCE_DIR`设置独立设置和歌词目录、独立实例标识。退出后结果位于`output/`对应时间戳目录；Debug验证入口不会启动正式崩溃监护。已有Debug构建可追加`-SkipBuild`。

| 命令中的脚本与参数 | 验证内容 | 前置条件 |
| --- | --- | --- |
| `tools/test-hidden-panel.ps1` | 隐藏启动、窗口释放、动画/音频停止、快捷键恢复、设置同步、快速切换与保存 | 可用全局测试快捷键；不执行实际播放控制 |
| `tools/test-lyric-appearance.ps1` | 实际设置控件、字体迁移与分段、封面取色、白色回退、自定义色、混色/透明度、高亮保持与保存 | Windows字体与WinUI桌面 |
| `tools/test-lyric-stability.ps1 -Mode status` | 340/400/600宽度下空状态与唱片避让、封面颜色传输、时钟延迟补偿 | 无需实际播放器 |
| `tools/test-lyric-stability.ps1 -Mode lookup` | 标题先到、时长后修正时恢复查询，普通位置更新不追加请求 | 使用测试歌词来源 |
| `tools/test-lyric-stability.ps1 -Mode fonts` | 120次字体切换与强制GC | 本机字体；可用字体随电脑不同 |
| `tools/test-lyric-layout.ps1 -FixturePath <缓存文件>` | 切换、跳转、中途切句、唱片避让与过渡连续性 | 同步歌词JSON缓存，默认查找本机《Cheap Hotel》缓存 |

例如：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-hidden-panel.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-lyric-appearance.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-lyric-stability.ps1 -Mode status
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-lyric-stability.ps1 -Mode lookup
```

布局检查需要自备已获取的歌词缓存，不能假设全新克隆具有开发者缓存或字体：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-lyric-layout.ps1 -FixturePath 'C:\path\to\lyrics-cache.json' -ChineseFont 'Microsoft YaHei' -Font 'Arial'
```

`-NoObstacle`关闭绕排，便于对照；报告不包含歌词正文。不要将受版权保护的实际缓存提交到仓库。

## 实际媒体与故障恢复

先在支持系统媒体会话的播放器中打开歌曲。以下压力测试只读媒体状态，在测试实例的媒体子进程中注入异常，不执行播放控制：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-lyric-stability.ps1 -Mode fonts -WithMedia -CrashMedia
```

它模拟监听进程以`0xC0000374`退出，检查主进程存活、限次恢复与主进程的SMTC隔离。该检查不等于修复Windows原生组件。

发布后检查媒体传输：

```powershell
dotnet publish src/LaLaLand/LaLaLand.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained false -o publish/LaLaLand-win-x64
New-Item -ItemType Directory -Path output -Force | Out-Null
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-release-media.ps1
```

脚本需要实际歌曲及封面，检查完整快照、封面像素/配色和管道关闭后的正常退出。可用`-Executable`和`-ReportPath`指定相对仓库根目录的路径；报告文件的父目录需预先存在。

## 可选联网检查

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-lyrics.ps1 -Live
dotnet run --project tests/LaLaLand.Lyrics.Tests/LaLaLand.Lyrics.Tests.csproj -c Release -- --live-feedback
```

`-Live`检查《烟花易冷》的多源匹配与酷狗下载，`--live-feedback`检查代码内指定的五首歌曲，默认使用独立临时缓存。只输出匹配元数据，不输出歌词正文。不要追加`--warm-cache`做隔离验证：该参数会改用真实用户歌词缓存。

在线来源可能受网络、限流、曲库或接口变化影响，失败应与离线回归结果分开报告。联网成功只证明当时取得了匹配数据，不能证明与实际声音同步。

## 当前验证与验收边界

2026-10-13本次整理文档时，在本机Windows x64、.NET SDK 8.0.302上重新运行：

- Release构建：通过，0警告、0错误。
- 干净源码副本的Debug构建：通过，0警告、0错误。
- 默认x64目录发布：通过；未以此推断实际启动或媒体读取通过。
- 离线歌词回归：36/36通过。
- 文档静态检查：7份文档、39处本地链接与PowerShell示例语法通过；暂存差异无空白错误。

默认发布与歌词回归还在仅含待提交文件的干净源码副本中通过，发布目录的EXE、DLL、运行配置、依赖清单、PRI、WinUI库与图标均存在；不依赖原目录的`bin/`或`obj/`。

本次没有把历史`output/`报告上传为当前证据。UI集成、联网、ARM64、其他Windows版本及逐首听感对齐，需分别运行对应检查并记录结果；不得由上述结果推断全部通过。

代码变更按影响选择检查：歌词服务至少运行离线回归；隐藏生命周期运行隐藏检查；字体与颜色运行外观及相关布局检查；媒体进程运行实际媒体传输与恢复。文档变更检查命令、链接、路径与前置条件。
