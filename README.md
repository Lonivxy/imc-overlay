# IMC.RE Overlay

一个 Windows 战绩悬浮窗，支持 IMC.RE 的起床战争和 CounterEmber。使用 C# 和 WPF 编写。

![悬浮窗界面示例](docs/images/overlay.png)

*截图使用虚构玩家和示例数据。*

CounterEmber 查询还可打开 Rating 趋势和近期比赛明细：

![CounterEmber 近期比赛与 Rating 趋势](docs/images/counterember-details.png)

*查询明细截图使用虚构玩家和示例数据。*

## 使用

1. 从 Releases 下载 Windows x64 发布包并解压，运行 `IMCBedwarsOverlay-CSharp.exe`。
2. 在「设置」选择查询模式：初次使用默认为起床战争，之后会记住所选模式。可在此绑定「我的游戏 ID」，以及手动测试 `api.imc.re` 网络连通性。
3. 进入 IMC.RE 大厅，打开聊天框、输入空格并按 Tab，悬浮窗会尝试从客户端日志读取玩家名单。
4. 点击「天梯榜单」可直接浏览全服 Top 50 排行榜（默认按 Rating 排序）。若在设置中绑定了个人 ID，将在榜单最上方第一行置顶显示个人全服排名（即使排名在 50 名外也会固定置顶）。
5. 点击玩家行复制战绩，或使用「查询玩家」查询指定玩家。

软件启动时会自动在后台自检 `api.imc.re` 网络连通性。若正常连接则保持静默；若被系统防火墙、网络代理或 DNS 异常阻拦，将在主窗口顶部显示醒目的排查提醒。

查询成功后可以选择「用图片查看」，生成带玩家头像、模式数据和日期的战绩卡，再将 PNG 保存到「图片\IMC.RE」文件夹并自动复制到剪贴板。背景使用 Minecraft 官方壁纸，首次生成需要联网加载。

默认按 `\` 显示或隐藏悬浮窗（支持在设置中交互式录制任意按键或组合键，如 `F8`、`Ctrl + Tab`，支持一键恢复默认）；右上角 × 退出程序。顶栏支持一键「清空」同局列表；查询玩家支持最近历史记录快速重查。悬浮窗自动记忆停靠位置与窗口尺寸，并可在设置中开启极危玩家进场音效预警。可在设置中调整不透明度，并拖动窗口右下角手柄缩放。

起床战争显示胜率、MVP、FKDR、拆床和终杀；CounterEmber 显示 Rating、K/D、ADR 和爆头率。点击表头可排序。数据来自 IMC.RE 的公开战绩接口，头像通过 mc-heads.net 加载。

### 起床战争估算 Rating

起床战争 Rating 是本项目的启发式估算，**不是 IMC.RE 的官方评分**。它综合 FKDR、胜负比和 K/D，并对少量对局做样本量收缩。计算方式如下：

令 `n` 为对局数、`w` 为胜场数、`l = n - w` 为负场数、`f` 为 FKDR、`k` 为 K/D。FKDR 和 K/D 使用 IMC.RE 接口返回的比率；胜负比由胜场和负场计算。各比率的分母使用至少为 1 的平滑值：

$$
\mathrm{WLR} = \frac{w}{\max(1,l)}, \qquad
\mathrm{FKDR} = \frac{\text{final kills}}{\max(1,\text{final deaths})}, \qquad
\mathrm{KDR} = \frac{\text{kills}}{\max(1,\text{deaths})}.
$$

$$
\begin{aligned}
R_{\mathrm{FKDR}} &= 0.20 + 0.80\log_2(f+1), \\
R_{\mathrm{WLR}} &= 0.20 + 0.80\log_2(\mathrm{WLR}+1), \\
R_{\mathrm{KDR}} &= 0.25 + 0.75\log_2(\mathrm{KDR}+1), \\
\mathrm{Perf} &= 0.45R_{\mathrm{FKDR}} + 0.35R_{\mathrm{WLR}} + 0.20R_{\mathrm{KDR}}, \\
\mathrm{Conf}(n) &= \frac{n}{n+20}.
\end{aligned}
$$

$$
\mathrm{Rating}_{\mathrm{BW}} = \min\left(\max\left(
1 + \mathrm{Conf}(n)(\mathrm{Perf}-1)\times1.25 + 0.04\log_{10}(n+1),
0.10\right), 3.00\right).
$$

其中 `Conf(n)` 会降低低场次的表现影响；`0.04 log_{10}(n+1)` 是较小的经验场次加成。最终 Rating 限制在 0.10 到 3.00 之间。累计拆床数不直接提高评分。

少于 10 场时数字后显示 `*`，提示样本不足；无对局时不显示评分。威胁标签按显示到小数点后两位的估算值分级：1.35 及以上为「极危」、1.12 至 1.34 为「危险」，其他为「普通」。累计拆床数不直接提高评分。

表格按显示到小数点后两位的有效 Rating 给玩家整行着色：1.35 及以上为红色，1.12 至 1.34 为黄色，低于 1.12 或没有有效评分时保持普通背景。该着色规则同样适用于 CounterEmber 的官方 Rating；跨模式的评分不可直接比较。

评分仅用于快速参考，权重和分级尚未依据服务器玩家数据校准，不能代表真实水平或判定玩家身份。

日志句柄枚举使用 Windows 的 `NtQueryInformationProcess` 内部接口。微软说明该接口可能随 Windows 版本变化，因此程序在接口不可用或权限不足时会退回到游戏目录推测。[API 文档](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntqueryinformationprocess)。

## 构建

使用 Windows 和 .NET 10 SDK：

```powershell
dotnet publish .\IMCBedwarsOverlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

配置存放在 `%APPDATA%\IMCBedwarsOverlay\config.json`。程序会筛选运行中的 Minecraft `java.exe`/`javaw.exe` 客户端，优先从其正在写入的文件句柄定位 `latest.log`，支持 Badlion Client / BLC、Lunar、Feather 和自定义游戏目录。BLC 同时打开聊天日志和游戏日志时，会优先选择 `minecraft` 主日志；多个客户端同时运行时优先选择前台游戏。若 Windows 不允许读取进程句柄，只会按该 Java 进程自己的 `--gameDir` 推测日志位置；没有可确认的游戏目录时，不会回退到其他启动器或旧日志。

CounterEmber 查询会显示接口提供的胜率、胜场、MVP、Rating、最高 Rating、K/D、KAST、ADR、爆头率、击杀、下包和拆包数据。查询窗口还提供近 30 场 Rating 趋势与最近 15 场比赛明细，包括地图、模式、队伍、比分、结果及逐场数据。

回归检查：

```powershell
dotnet run --project .\tests\Overlay.Checks\Overlay.Checks.csproj -c Release
```

## 许可证与发布文件校验

本项目代码使用 [GPL-3.0-only](LICENSE)，第三方代码许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。Minecraft 壁纸、玩家头像和相关商标属于各自权利人，不因本项目许可证获得授权；请遵守 [Minecraft 壁纸页面的使用条款](https://www.minecraft.net/en-us/collectibles)。

新版 Release 同时提供 ZIP 和对应的 `.sha256` 文件。可在 PowerShell 中运行 `Get-FileHash .\IMC-Overlay-版本号-win-x64.zip -Algorithm SHA256`，并与 `.sha256` 文件中的值比较。校验和用于检查文件是否一致，不单独证明发布者身份。

## 发布新版本

将版本标签推送到 GitHub 后，Actions 会自动构建独立的 Windows x64 程序、打包并创建 Release。例如：

```powershell
git tag v2.1-radar
git push origin v2.1-radar
```

测试与探测标签（例如 `v1.3-probe`）会发布为预发行版本，正式版本标签（例如 `v2.1-radar`、`v2.0.0`）会发布为正式发行版（Release）。普通分支推送不会自动创建 Release。

也可在 GitHub Actions 中手动运行工作流验证构建；手动运行只上传供下载的 Actions 构建产物，不创建 Release。
