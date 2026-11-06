using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using WattMonitor.Core;

namespace WattMonitor.App.Controls;

internal static class Localization
{
    private static readonly Dictionary<string, string> english = new(StringComparer.Ordinal)
    {
        ["悬浮球样式"] = "Floating orb style", ["科技圆环"] = "Tech ring", ["简洁纯色"] = "Solid", ["半透明玻璃"] = "Translucent glass", ["悬浮球尺寸"] = "Floating orb size", ["恢复默认尺寸"] = "Restore default size", ["面积"] = "Area",
        ["功率"] = "Power", ["内存"] = "Memory", ["磁盘"] = "Disk", ["网络"] = "Network",
        ["— 不可用"] = "— Unavailable", ["不可用"] = "Unavailable",
        ["等待采集"] = "Waiting for data", ["等待记录"] = "Waiting for records", ["等待本机采集"] = "Waiting for local data", ["正在连接采集源"] = "Connecting to the sampling source",
        ["等待读数"] = "Waiting for a reading", ["等待数据"] = "Waiting for data", ["等待可用采样"] = "Waiting for samples",
        ["总览"] = "Overview", ["实时监测"] = "Live monitor", ["用电记录"] = "History", ["设置"] = "Settings",
        ["能耗监测"] = "Energy monitor", ["打开快览面板"] = "Open quick view", ["退出并停止记录"] = "Quit and stop recording",
        ["重试连接"] = "Retry connection", ["打开文件位置"] = "Open file location", ["快速监测"] = "Quick view",
        ["固定"] = "Pin", ["打开主窗口"] = "Open main window", ["确定"] = "OK", ["取消"] = "Cancel",
        ["保存"] = "Save", ["不保存"] = "Don't save", ["强制结束"] = "Force quit", ["是"] = "Yes", ["否"] = "No",
        ["让每一瓦，都有迹可循"] = "See where every watt goes",
        ["主机功率按硬件读数与电源效率估算，建议使用插座功率计校准。退出或休眠期间不会补记。"] = "PC power is estimated from hardware readings and power supply efficiency. Calibrate with a plug-in power meter. Time spent closed or asleep is not recorded.",
        ["了解，开始使用"] = "Got it, get started", ["主机估算功率"] = "Estimated PC power", ["今日预计电费"] = "Estimated cost today",
        ["查看功率详情 →"] = "View power details →", ["查看用电记录 →"] = "View energy history →", ["查看实时详情 →"] = "View live details →",
        ["硬件状态"] = "Hardware status", ["专用显存"] = "Dedicated GPU memory", ["磁盘吞吐"] = "Disk throughput", ["网络吞吐"] = "Network throughput",
        ["读取 + 写入"] = "Read + write", ["接收 + 发送"] = "Receive + send",
        ["功率与电费均为估算；仅累计功率完整的已采样时段。"] = "Power and cost are estimates; only samples with complete power readings are included.",
        ["当前读数"] = "Current reading", ["应用排行"] = "Top applications", ["估算依据"] = "Estimate details", ["图形处理器"] = "Graphics processor",
        ["磁盘活动"] = "Disk activity", ["网络活动"] = "Network activity", ["使用情况"] = "Usage", ["GPU 使用率"] = "GPU usage",
        ["横轴为本地时间；断线代表缺失采样。移动鼠标查看实际时间与数值。"] = "The horizontal axis shows local time; gaps indicate missing samples. Hover to see the time and value.",
        ["实时趋势图，移动鼠标查看采样时间与数值"] = "Live trend chart; move the pointer to inspect sample time and value.",
        ["正常关闭"] = "Close normally", ["强制结束…"] = "Force quit…", ["更多进程操作"] = "More process actions",
        ["显示选项即时生效；估算与电价修改需单独保存。"] = "Display changes take effect immediately. Save estimation and tariff changes separately.",
        ["实时预览"] = "Live preview", ["预览随当前采样与设置更新"] = "Updates with the current readings and settings", ["任务栏"] = "Taskbar",
        ["外观"] = "Appearance", ["界面主题与动态效果"] = "Theme, language and motion", ["主题"] = "Theme",
        ["关于与更新"] = "About & updates", ["查看当前版本并检查新版本"] = "View the current version and check for updates",
        ["下载安装更新"] = "Download and install update", ["打开发布页面"] = "Open releases page",
        ["语言"] = "Language", ["跟随系统"] = "Follow system", ["简体中文"] = "Simplified Chinese", ["英语"] = "English",
        ["深色"] = "Dark", ["浅色"] = "Light", ["减少动态效果"] = "Reduce motion",
        ["采集"] = "Sampling", ["影响实时刷新与应用排行显示"] = "Controls live updates and the application ranking", ["刷新间隔"] = "Refresh interval",
        ["1 秒"] = "1 second", ["2 秒"] = "2 seconds", ["5 秒"] = "5 seconds", ["前 3 项"] = "Top 3", ["前 10 项"] = "Top 10", ["前 20 项（快览最多 10 项）"] = "Top 20 (quick view: 10 maximum)",
        ["开机启动"] = "Start with Windows", ["修复采集服务"] = "Repair sampling service", ["需要管理员授权；CPU 功率还需要 PawnIO 驱动。"] = "Administrator permission is required. CPU power also requires the PawnIO driver.",
        ["桌面组件"] = "Desktop widgets", ["设置悬浮圆环、快览和任务栏指标"] = "Configure the floating ring, quick view and taskbar metrics",
        ["显示悬浮圆环"] = "Show floating ring", ["圆环显示指标"] = "Floating ring metric", ["固定快览（点击外部不收起）"] = "Pin quick view (keep open when clicking elsewhere)",
        ["显示任务栏指标"] = "Show taskbar metrics", ["重新检测任务栏"] = "Retry taskbar detection", ["任务栏指标与次序"] = "Taskbar metrics and order",
        ["拖动行可排序；键盘使用 Alt + ↑ / ↓ 调整，空格切换显示。"] = "Drag rows to reorder. Use Alt + ↑ / ↓ to move and Space to toggle visibility.",
        ["拖动  ·  Alt + ↑ / ↓"] = "Drag  ·  Alt + ↑ / ↓",
        ["估算与电价"] = "Estimation and tariff", ["仅影响保存后的新采样；历史电费保留原电价"] = "Applies to new samples after saving; historical costs retain their original tariff",
        ["其他部件补偿（W）"] = "Other components allowance (W)", ["电源效率（0.5–1）"] = "Power supply efficiency (0.5–1)",
        ["添加或更新电价"] = "Add or update tariff", ["电价（元 / kWh）"] = "Tariff (CNY / kWh)", ["生效日期（本地 00:00）"] = "Effective date (local 00:00)", ["保存估算与电价"] = "Save estimation and tariff",
        ["选择方块查看当日详情"] = "Select a square for daily details", ["按日期查看电脑的估算用电和费用。"] = "View estimated energy use and costs by date.",
        ["今日"] = "Today", ["近 7 天"] = "Last 7 days", ["本月"] = "This month", ["近一年"] = "Last year", ["自选日期"] = "Custom dates", ["从  "] = "From  ", ["  至  "] = "  to  ",
        ["查询"] = "Search", ["导出 CSV"] = "Export CSV", ["估算电费"] = "Estimated cost", ["沿用记录时的电价"] = "Uses the tariff at recording time", ["估算电量"] = "Estimated energy", ["仅包含功率完整的采样"] = "Only samples with complete power readings", ["记录覆盖"] = "Recording coverage",
        ["每日用电"] = "Daily energy", ["颜色越深表示估算电量越高；斜线表示没有完整功率记录。"] = "Darker colors mean more estimated energy; hatching means no complete power record.",
        ["柱状"] = "Bars", ["折线"] = "Line", ["曲线"] = "Smooth", ["用电趋势"] = "Energy trend", ["每日明细"] = "Daily details",
        ["未记录仅统计可以核实的电脑运行时间；证据不足时显示未知。"] = "Unrecorded time only includes verifiable PC uptime; unknown is shown when evidence is insufficient.",
        ["日期"] = "Date", ["电量"] = "Energy", ["电费"] = "Cost", ["已记录"] = "Recorded", ["功率完整"] = "Complete power", ["运行中未记录"] = "Unrecorded while running"
        , ["5 分"] = "5 min", ["15 分"] = "15 min", ["60 分"] = "60 min", ["5 分钟"] = "5 min", ["15 分钟"] = "15 min", ["60 分钟"] = "60 min"
        , ["☰   功率"] = "☰   Power", ["☰   内存"] = "☰   Memory", ["☰   磁盘"] = "☰   Disk", ["☰   网络"] = "☰   Network"
        , ["功率，按 Alt 加上下方向键排序"] = "Power, press Alt and an arrow key to reorder", ["CPU，按 Alt 加上下方向键排序"] = "CPU, press Alt and an arrow key to reorder", ["GPU，按 Alt 加上下方向键排序"] = "GPU, press Alt and an arrow key to reorder", ["内存，按 Alt 加上下方向键排序"] = "Memory, press Alt and an arrow key to reorder", ["磁盘，按 Alt 加上下方向键排序"] = "Disk, press Alt and an arrow key to reorder", ["网络，按 Alt 加上下方向键排序"] = "Network, press Alt and an arrow key to reorder"
        , ["未保存的修改"] = "Unsaved changes", ["强制结束进程"] = "Force quit process"
        , ["能耗与性能"] = "Energy and performance", ["显示指标"] = "Display metric", ["隐藏悬浮圆环"] = "Hide floating ring", ["打开 WattHalo"] = "Open WattHalo", ["WattHalo 悬浮监测"] = "WattHalo floating monitor"
        , ["固定后点击其他窗口不会自动收起。"] = "When pinned, clicking another window will not close the panel.", ["收起快速监测"] = "Close quick view", ["点击展开快速监测；拖动调整位置；右键切换指标。"] = "Click to open quick view; drag to move; right-click to change the metric.", ["按 Enter 展开快速监测，右键切换显示指标。"] = "Press Enter to open quick view; right-click to change the metric."
        , ["已固定"] = "Pinned", ["记录失败"] = "Recording failed", ["已过期"] = "Stale", ["W · 估算"] = "W · estimated", ["等待采样"] = "Waiting for a sample", ["暂无用电记录"] = "No energy records", ["自动检查更新"] = "Check for updates automatically", ["检查更新"] = "Check for updates"
        , ["最近采集趋势；断线表示该时段读数不可用。纵轴按当前范围缩放。"] = "Recent sampling trend; gaps indicate unavailable readings. The vertical axis scales to the current range."
        , ["正在准备"] = "Preparing", ["已隐藏"] = "Hidden", ["已嵌入任务栏"] = "Embedded in taskbar", ["任务栏组件初始化失败，正在重试"] = "Taskbar widget initialization failed; retrying", ["未找到 Windows 任务栏，正在重试"] = "Windows taskbar not found; retrying", ["无法创建任务栏组件，正在重试"] = "Cannot create taskbar widget; retrying", ["无法嵌入 Windows 任务栏，正在重试"] = "Cannot embed in Windows taskbar; retrying", ["任务栏组件暂不可见，正在重试"] = "Taskbar widget temporarily hidden; retrying", ["无法读取任务栏布局，正在重试"] = "Cannot read taskbar layout; retrying", ["竖直任务栏暂不支持嵌入，正在重试"] = "Embedding in a vertical taskbar is not supported; retrying", ["无法确认任务栏控件位置，正在重试"] = "Cannot locate taskbar buttons; retrying", ["无法确认任务栏通知区域，正在重试"] = "Cannot locate taskbar notification area; retrying", ["任务栏没有足够的安全空间，正在重试"] = "Not enough safe space on the taskbar; retrying"
        , ["系统CPU占用读取失败"] = "Unable to read system CPU usage", ["CPU Package功率不可用；请确认PawnIO驱动、CPU支持情况及采集服务权限"] = "CPU package power is unavailable; check the PawnIO driver, CPU support and sampling service permissions", ["系统内存读取失败"] = "Unable to read system memory", ["无法完整识别显卡，主机功率估算暂不可用。"] = "Graphics adapters could not be fully identified; PC power estimates are unavailable.", ["磁盘吞吐计数器不可用"] = "Disk throughput counters unavailable", ["网络吞吐读取失败"] = "Unable to read network throughput", ["无法打开GPU性能查询"] = "Unable to open GPU performance query", ["GPU引擎计数器不可用（需要WDDM驱动）"] = "GPU engine counters unavailable (a WDDM driver is required)", ["进程GPU占用不可用"] = "Per-process GPU usage unavailable", ["进程专用显存计数器不可用"] = "Per-process dedicated GPU memory counters unavailable", ["未找到NVIDIA NVML；独立显卡功率不可用"] = "NVIDIA NVML not found; discrete GPU power unavailable"
        , ["该进程不允许从监测工具关闭。"] = "This process cannot be closed from the monitor.", ["目标已变化或不属于当前会话，请刷新后重试。"] = "The target changed or belongs to another session. Refresh and try again.", ["无法核对目标软件路径，操作已取消。"] = "The application path could not be verified; the operation was cancelled.", ["Windows 关键进程不可关闭。"] = "Critical Windows processes cannot be closed.", ["无法验证进程所有者，操作已取消。"] = "The process owner could not be verified; the operation was cancelled.", ["只能关闭当前用户启动的软件。"] = "Only applications started by the current user can be closed.", ["已请求正常关闭，请在目标软件中处理保存提示。"] = "A normal close was requested. Respond to any save prompts in the application.", ["目标没有可关闭的主窗口或未接受请求。需要时可单独选择强制结束。"] = "The application has no closable main window or did not accept the request. You can force quit if necessary.", ["该进程已经退出。"] = "The process has already exited.", ["该进程已经退出或状态发生变化。"] = "The process has exited or its state changed.", ["权限不足，未关闭目标软件。"] = "Insufficient permissions; the application was not closed.", ["请至少保留一个任务栏指标。"] = "Keep at least one taskbar metric."
    };
    private static readonly HashSet<string> resources = new(StringComparer.Ordinal);
    private const string Prefix = "Ui.Localization.";
    private static LanguagePreference preference = LanguagePreference.System;
    private static bool isEnglish = !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
    private static bool watchingSystem;

    public static event EventHandler? Changed;
    public static bool IsEnglish => isEnglish;
    public static string Text(string chinese) => isEnglish && english.TryGetValue(chinese, out var value) ? value : chinese;
    public static string Text(string chinese, string translated) => isEnglish ? translated : chinese;
    public static string Diagnostic(string source)
    {
        if (!isEnglish) return source;
        var exact = Text(source);
        if (exact != source) return exact;
        const string gpuSuffix = "功率不可用，不参与完整功率估算";
        if (source.EndsWith(gpuSuffix, StringComparison.Ordinal)) return source[..^gpuSuffix.Length] + " power unavailable; excluded from complete power estimates";
        foreach (var (prefix, translated) in new[]
        {
            ("CPU传感器初始化失败：", "CPU sensor initialization failed: "), ("CPU传感器读取失败：", "CPU sensor read failed: "),
            ("NVML接口不可用：", "NVML interface unavailable: "), ("NVML初始化失败（状态", "NVML initialization failed (status "),
            ("NVML枚举失败（状态", "NVML enumeration failed (status "), ("已嵌入任务栏 · 已折叠 ", "Embedded in taskbar · collapsed "),
            ("已请求强制结束 ", "Force quit requested for ")
        })
            if (source.StartsWith(prefix, StringComparison.Ordinal)) return translated + source[prefix.Length..].Replace("（PID ", " (PID ", StringComparison.Ordinal).Replace("）。", ").", StringComparison.Ordinal).Replace("）", ")", StringComparison.Ordinal).Replace(" 项", " item(s)", StringComparison.Ordinal);
        return source;
    }

    public static void Bind(DependencyObject element, DependencyProperty property, string chinese)
    {
        if (element is not FrameworkElement target || Application.Current is not { } app || !english.ContainsKey(chinese))
        { element.SetValue(property, Text(chinese)); return; }
        if (resources.Add(chinese) || !app.Resources.Contains(Prefix + chinese)) app.Resources[Prefix + chinese] = Text(chinese);
        target.SetResourceReference(property, Prefix + chinese);
    }

    public static void Apply(LanguagePreference value)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Apply(value)); return; }
        preference = Enum.IsDefined(value) ? value : LanguagePreference.System;
        if (!watchingSystem)
        {
            SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
            watchingSystem = true;
        }
        var next = preference switch { LanguagePreference.Chinese => false, LanguagePreference.English => true, _ => !SystemLanguageIsChinese() };
        if (next == isEnglish) return;
        isEnglish = next;
        if (app is not null) foreach (var key in resources.ToArray()) app.Resources[Prefix + key] = Text(key);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool SystemLanguageIsChinese()
    {
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase); }
        catch (CultureNotFoundException) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase); }
    }
    private static void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (preference != LanguagePreference.System) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => Apply(LanguagePreference.System)));
    }
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
