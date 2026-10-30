# IMC.RE Overlay

一个 Windows 战绩悬浮窗，支持 IMC.RE 的起床战争和 CounterEmber。使用 C# 和 WPF 编写。

## 使用

1. 从 Releases 下载 Windows x64 发布包并解压，运行 `IMCBedwarsOverlay-CSharp.exe`。
2. 在「设置」选择查询模式：初次使用默认为起床战争，之后会记住所选模式。
3. 进入 IMC.RE 大厅，打开聊天框、输入空格并按 Tab，悬浮窗会尝试从客户端日志读取玩家名单。
4. 点击玩家行复制战绩，或使用「查询玩家」查询指定玩家。

查询成功后可以选择「用图片查看」，生成带玩家头像、模式数据和日期的战绩卡，再将 PNG 保存到「图片\IMC.RE」文件夹并自动复制到剪贴板。背景使用 Minecraft 官方壁纸，首次生成需要联网加载。

按 `\` 显示或隐藏悬浮窗；右上角 × 退出程序。可在设置中调整不透明度，并拖动窗口右下角的手柄按比例调整大小。

起床战争显示胜率、MVP、FKDR、拆床和终杀；CounterEmber 显示 Rating、K/D、ADR 和爆头率。点击表头可排序。数据来自 IMC.RE 的公开战绩接口，头像通过 mc-heads.net 加载。

## 构建

使用 Windows 和 .NET 10 SDK：

```powershell
dotnet publish .\IMCBedwarsOverlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

配置存放在 `%APPDATA%\IMCBedwarsOverlay\config.json`。游戏客户端的日志位置和实际扫描行为取决于客户端版本；如自动扫描未识别，可使用「查询玩家」。

## 发布新版本

将版本标签推送到 GitHub 后，Actions 会自动构建独立的 Windows x64 程序、打包并创建 Release。例如：

```powershell
git tag v1.0.1
git push origin v1.0.1
```

包含连字符的版本标签（例如 `v1.1-probe`）会发布为预发行版本。普通分支推送不会自动创建 Release。

也可在 GitHub Actions 中手动运行工作流验证构建；手动运行只上传供下载的 Actions 构建产物，不创建 Release。
