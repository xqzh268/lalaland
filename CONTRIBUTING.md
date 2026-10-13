# 贡献指南

感谢帮助改进LaLaLand。开始前阅读[开发与发布](docs/development.md)、[架构说明](docs/architecture.md)与[测试说明](docs/testing.md)。项目沿用[AGPL-3.0许可证](LICENSE)。

## 问题与建议

通过[Issues](https://github.com/xqzh268/lalaland/issues)描述问题或具体使用场景。问题报告包含系统、架构、程序版本/提交、播放器、复现步骤和预期行为；歌词与界面问题所需信息见[故障排查](docs/troubleshooting.md#报告问题)。

不要在公开报告中上传API凭据、未检查的用户日志、实际歌词缓存或完整用户目录。日志与截图只保留解释问题所需的内容。

## 代码变更

- 沿用现有C#、XAML和分部类组织，优先在对应服务或控件中实现职责。
- UI操作回到Dispatcher线程；后台获取支持取消，窗口释放时解除订阅、停止计时器和音频分析。
- SMTC原生对象保留在媒体子进程，主进程消费托管快照；媒体子进程不保存用户设置。
- 歌词优先校验歌曲与录音身份，不用更完整的时间轴掩盖错歌手或错版本；失败与取消不写入长期无歌词缓存。
- 设置迁移保留已有偏好；时间校准与歌词跳转维持同一可逆模型。
- 添加来源、持久化字段、快捷键或新的后台任务时更新相应文档。

## 验证与提交

1. 创建说明用途的分支，使一次提交围绕一个具体问题。
2. 运行Release构建，以及[测试说明](docs/testing.md)中与影响范围相关的检查。修复逻辑问题时提供能证明行为的回归；文档或低影响样式变更无需添加重复实现的测试。
3. 执行`git diff --check`并审查暂存文件，排除构建目录、验证产物、用户设置和歌词正文。
4. 提交PR，说明问题、修改后的行为、运行的验证及未验证边界。标明联网、实际播放器、UI或ARM64检查是否运行。

```powershell
dotnet build src/LaLaLand/LaLaLand.csproj -c Release -p:Platform=x64
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-lyrics.ps1
git diff --check
git status --short
```

避免无关的依赖升级与全仓格式化。发布文件应在目标Windows环境验证实际启动、设置保存与媒体读取；构建成功不能替代运行验收。
