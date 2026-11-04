# WattHalo · 瓦环

WattHalo 是一个 Windows 桌面能耗与性能监测工具。它把电脑的估算功率、电费和 CPU、GPU 等资源状态放在同一个地方，也可以通过悬浮圆环、快览面板和任务栏查看。

WattHalo is a local-first Windows monitor for estimated PC power, energy costs and hardware activity. The interface supports Simplified Chinese, English and the Windows display language.

## 能做什么

- 查看主机估算功率、今日电量与电费，以及 CPU、GPU、显存、内存、磁盘和网络状态。
- 在悬浮圆环显示选定指标；点击打开快览，拖动圆环时快览跟随。主窗口提供趋势和应用排行。
- 在 Windows 任务栏显示自选指标；CPU、GPU 可同时显示占用率与功率。任务栏空间不足时按设置顺序折叠，无法嵌入时显示原因并重试。
- 用热力方块、柱状图、折线或曲线查看用电历史，按今日、近七天、本月、近一年或自选日期筛选，支持 CSV 导出。
- 提供深色、浅色和跟随系统主题；支持简体中文、英文及跟随系统语言。
- 可设置登录后启动。关闭主窗口继续托盘记录；选择「退出并停止记录」后停止记录。

## 安装

在 [Releases](https://github.com/H-Bole/WattHalo/releases) 下载 `WattHalo-win-x64.zip`，解压后运行 `Install.cmd`。已有安装会沿用原目录；首次安装默认使用系统的 `Program Files\WattMonitor`。安装程序会请求管理员权限以配置硬件采集服务，日常界面使用普通用户权限运行。

CPU 功率读取使用 [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 和 [PawnIO](https://github.com/namazso/PawnIO.Setup)。已有 PawnIO 时不会重复安装；没有时安装程序会给出官方获取方式。硬件或驱动不提供的读数显示为不可用，不按零计算。

可从设置检查更新；程序也会定期检查 GitHub 上的正式版本，并用发行者固定公钥验证更新信息。应用内下载安装仍在验证受保护目录下的恢复流程，目前不可用；有新版本时请从本仓库的正式 Release 获取完整安装包。完整安装包尚未使用 Windows Authenticode 代码签名。

## 理解功率和电费

**WattHalo 显示的是软件估算值，不是经过校准的电表。** 默认统计电脑主机，不包括显示器及房间内其他电器；它不能直接代替房东的整间房电表。

```text
主机估算功率 =（CPU 封装功率 + 独立显卡功率 + 其他部件补偿）÷ 电源效率
```

默认其他部件补偿为 30 W、电源效率为 90%、电价为 1 元 / kWh。它们是可调整的初始假设；建议用插座功率计校准。核显通常已包含在 CPU 封装功率中，不重复相加。CPU 或已识别独显的功率缺失时，不估算该时段的整机电费。

历史记录沿用采样时的电价，不因后来修改电价而重算。页面分别显示已记录时长、功率完整时长，以及能核实的「电脑运行中未记录」时长。系统事件或历史证据不足时显示未知；休眠、关机、退出和无法确认的缺口不会补造电量。

分钟数据保留 90 天，之后保留每日汇总，因此较旧记录不能还原小时曲线或缺口的准确位置。自选范围按本地日期查询。

## 数据与隐私

历史和设置保存在当前用户的 `%LOCALAPPDATA%\WattMonitor`，升级或卸载程序不会删除它们；共享的 PawnIO 驱动也会保留。软件不会上传硬件采样、进程列表或用电历史。检查更新时会访问 GitHub，GitHub 可收到正常网络请求所需的连接信息；CSV 仅在用户主动导出时写入所选位置。

任务栏嵌入依赖 Windows Shell 的行为，不是稳定的公开扩展接口。Windows 版本、任务栏空间和其他组件可能影响可用性；失败时不会额外创建悬浮条。

## 开发

需要 Windows 11 x64、.NET 10 SDK 和 NuGet 网络访问：

```powershell
dotnet build WattMonitor.slnx
dotnet run --project src/WattMonitor.App
```

使用隔离数据目录可以避免写入正式历史与自启动设置：

```powershell
dotnet run --project src/WattMonitor.App -- --data-dir "D:\WattHalo-test-data"
```

社区开发者可使用 `pwsh -File packaging/Build.ps1 -Unsigned` 创建本地安装包；该模式不生成官方签名更新资产。正式更新资产由维护者在本机使用受 Windows 用户保护的私钥签名，仓库只包含公钥。

## 开源许可与参考

本项目代码使用 [MIT License](LICENSE)，依赖及其许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。产品交互参考 [Magpie](https://github.com/yetone/magpie)，任务栏实现参考 [AgentSpeed](https://github.com/zoefix/agent-speed)，能耗功能研究参考 [WattSeal](https://github.com/Daminoup88/WattSeal)。项目没有复制 WattSeal 的 GPL 代码。
