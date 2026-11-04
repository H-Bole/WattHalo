using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WattMonitor.App.Controls;
using WattMonitor.App.Views;
using WattMonitor.Core;

namespace WattMonitor.App;

public sealed class AppController : IDisposable
{
    private readonly string _settingsPath;
    private readonly EnergyStore _store;
    private readonly object _storeGate = new();
    private readonly Queue<TelemetrySnapshot> _trend = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _settingsTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly UpdateCoordinator _updates = new();
    private CancellationTokenSource? _sampling;
    private Task? _samplingTask;
    private MainWindow? _main;
    private OrbWindow? _orb;
    private PanelWindow? _panel;
    private TaskbarWidget? _taskbar;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly System.Windows.Forms.ContextMenuStrip _trayMenu;
    private bool _exiting;
    private bool _storageFailed;
    private string? _storageError;
    private string? _samplingError;
    private long _lastReceipt;
    private bool _staleLatched;
    private int _freshAfterStale;
    private bool _settingsDirty;
    private bool _settingsBackupNeeded;
    private AppSettings _persistedSettings;
    private OperationResult? _pendingNotice;
    private readonly bool _isolated;
    private readonly object _presentationGate = new();
    private PendingPresentation _pendingPresentation = new();
    private bool _presentationQueued;
    private long _overflowRevision;

    private sealed record RecordItem(TelemetrySnapshot Sample, AppSettings Settings, bool ResetBaseline, long OverflowRevision);
    private sealed class PendingPresentation
    {
        public TelemetrySnapshot? Sample;
        public long Receipt;
        public EnergyTotals? Today, Week, Month;
        public bool StorageChanged, SamplingChanged;
        public string? StorageError, SamplingError;
    }

    public AppSettings Settings { get; private set; }
    public TelemetrySnapshot? Snapshot { get; private set; }
    public EnergyTotals Today { get; private set; } = new(0, 0, 0, 0, 0);
    public EnergyTotals Week { get; private set; } = new(0, 0, 0, 0, 0);
    public EnergyTotals Month { get; private set; } = new(0, 0, 0, 0, 0);
    public IReadOnlyList<TelemetrySnapshot> Trend => _trend.ToArray();
    public string Status { get; private set; } = "正在连接采集源…";
    public string TaskbarMode { get; private set; } = "正在准备";
    public bool IsRecording => MonitorStatus.IsRecording;
    internal bool RepairingService { get; private set; }
    internal UpdateCoordinator Updates => _updates;
    // Installation is enabled only once the privileged recovery path has passed validation.
    internal bool UpdateInstallAvailable { get; set; }
    public AppStatus MonitorStatus
    {
        get
        {
            var threshold = Math.Max(5, 3 * Settings.RefreshSeconds);
            var staleNow = Snapshot is not null && _lastReceipt != 0 &&
                (Stopwatch.GetElapsedTime(_lastReceipt).TotalSeconds > threshold ||
                 (DateTimeOffset.UtcNow - Snapshot.Timestamp).TotalSeconds > threshold);
            if (staleNow) { _staleLatched = true; _freshAfterStale = 0; }
            var stale = staleNow || _staleLatched;
            return new(stale, !_exiting && !_storageFailed && _samplingError is null && Snapshot is not null && !stale,
                Snapshot?.PowerComplete == true, Snapshot?.Mode ?? "正在连接采集源", _storageError,
                Snapshot?.Timestamp, _samplingError);
        }
    }
    public event EventHandler? Updated;
    public event Action<OperationResult>? Notice;

    public void Notify(OperationResult result)
    {
        if (result.Cancelled) return;
        if ((_main?.IsVisible != true || _main.WindowState == WindowState.Minimized) &&
            (_panel?.IsVisible != true || _panel.WindowState == WindowState.Minimized)) _pendingNotice = result;
        else Notice?.Invoke(result);
    }

    private void DeliverPendingNotice()
    {
        if (_pendingNotice is not { } notice) return;
        _pendingNotice = null;
        Notice?.Invoke(notice);
    }

    public AppController(string? dataDirectory = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _isolated = dataDirectory is not null;
        var directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattMonitor");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
        Settings = LoadSettings();
        _persistedSettings = Settings;
        Ui.ApplyTheme(Settings.Theme);
        Ui.ApplyLanguage(Settings.Language);
        _store = new EnergyStore(Path.Combine(directory, "history.db"));
        _settingsTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        _settingsTimer.Tick += (_, _) =>
        {
            _settingsTimer.Stop();
            if (!_settingsDirty) return;
            var result = SaveSettings(Settings);
            if (!result.Success) Notify(result);
        };
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) =>
        {
            var state = MonitorStatus;
            if (state.IsStale || state.SamplingError is not null)
            {
                RefreshStatus();
                Updated?.Invoke(this, EventArgs.Empty);
            }
        };
        _statusTimer.Start();
        _updates.Changed += UpdatesChanged;
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromHours(24) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "WattHalo · 正在连接", Visible = true
        };
        var menu = _trayMenu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("打开 WattHalo", null, (_, _) => ShowMain());
        menu.Items.Add("展开快速监测", null, (_, _) => TogglePanel());
        menu.Items.Add("显示／隐藏悬浮圆环", null, (_, _) => QueueSettings(current => current with { ShowOrb = !current.ShowOrb }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出并停止记录", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
        Ui.ThemeChanged += TrayThemeChanged;
        Ui.LanguageChanged += TrayLanguageChanged;
        TrayThemeChanged(this, EventArgs.Empty);
        TrayLanguageChanged(this, EventArgs.Empty);
    }

    private void TrayThemeChanged(object? sender, EventArgs e)
    {
        if (SystemParameters.HighContrast)
        {
            _trayMenu.Renderer = new System.Windows.Forms.ToolStripSystemRenderer();
            _trayMenu.BackColor = System.Drawing.SystemColors.Menu;
            _trayMenu.ForeColor = System.Drawing.SystemColors.MenuText;
            return;
        }
        var dark = Ui.IsDark;
        _trayMenu.Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new TrayColors(dark));
        _trayMenu.BackColor = dark ? System.Drawing.Color.FromArgb(32, 35, 36) : System.Drawing.Color.White;
        _trayMenu.ForeColor = dark ? System.Drawing.Color.FromArgb(241, 243, 242) : System.Drawing.Color.FromArgb(30, 39, 35);
    }

    private void TrayLanguageChanged(object? sender, EventArgs e)
    {
        _trayMenu.Items[0].Text = Ui.T("打开 WattHalo", "Open WattHalo");
        _trayMenu.Items[1].Text = Ui.T("展开快速监测", "Open quick view");
        _trayMenu.Items[2].Text = Ui.T("显示／隐藏悬浮圆环", "Show or hide the floating ring");
        _trayMenu.Items[4].Text = Ui.T("退出并停止记录", "Quit and stop recording");
        RefreshStatus();
    }

    private sealed class TrayColors(bool dark) : System.Windows.Forms.ProfessionalColorTable
    {
        private System.Drawing.Color Surface => dark ? System.Drawing.Color.FromArgb(32, 35, 36) : System.Drawing.Color.White;
        private System.Drawing.Color Selection => dark ? System.Drawing.Color.FromArgb(52, 62, 59) : System.Drawing.Color.FromArgb(231, 243, 238);
        private System.Drawing.Color Line => dark ? System.Drawing.Color.FromArgb(64, 71, 70) : System.Drawing.Color.FromArgb(211, 220, 216);
        public override System.Drawing.Color ToolStripDropDownBackground => Surface;
        public override System.Drawing.Color MenuBorder => Line;
        public override System.Drawing.Color MenuItemBorder => Selection;
        public override System.Drawing.Color MenuItemSelected => Selection;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Selection;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Selection;
        public override System.Drawing.Color SeparatorDark => Line;
        public override System.Drawing.Color SeparatorLight => Surface;
    }

    public void Start(bool background)
    {
        ApplyWidgets();
        if (!background || !Settings.OnboardingCompleted) ShowMain();
        RestartSampling();
        if (!_isolated) { _updateTimer.Start(); _ = CheckForUpdatesAsync(false); }
    }

    private void UpdatesChanged(object? sender, EventArgs e) => Updated?.Invoke(this, EventArgs.Empty);

    internal async Task CheckForUpdatesAsync(bool interactive)
    {
        var previousVersion = _updates.Release?.Version;
        var available = await _updates.CheckAsync();
        if (available && _updates.Release?.Version != previousVersion)
            Notify(new(true, string.Format(Ui.T("发现新版本 {0}，可在设置中查看。", "Version {0} is available in Settings."), _updates.Release!.Version)));
        else if (interactive && _updates.Stage == UpdateStage.Failed)
            Notify(new(false, _updates.Status));
    }

    internal void OpenReleases()
    {
        try { Process.Start(new ProcessStartInfo(WattMonitor.App.Updates.UpdateClient.ReleasesUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Notify(new(false, Ui.T("无法打开发布页面：", "Could not open the release page: ") + ex.Message)); }
    }

    internal async Task InstallUpdateAsync()
    {
        if (!UpdateInstallAvailable || _updates.Busy || _updates.Release is null) return;
        if (_updates.Stage != UpdateStage.Ready && !await _updates.DownloadAsync()) return;
        if (_main is not null && !_main.ConfirmBeforeExit()) return;
        if (_settingsDirty)
        {
            var saved = SaveSettings(Settings);
            if (!saved.Success) { Notify(saved); return; }
        }
        var (launched, failure) = await _updates.LaunchAsync();
        if (launched) Quit();
        else if (!string.IsNullOrEmpty(failure)) Notify(new(false, failure));
    }

    private AppSettings LoadSettings()
    {
        if (!File.Exists(_settingsPath)) return new();
        try { return Validate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new()); }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            Status = "设置文件无法读取，暂用默认设置；原文件未覆盖。";
            _settingsBackupNeeded = true;
            return new();
        }
    }

    private static AppSettings Validate(AppSettings value)
    {
        if (value.RefreshSeconds is not (1 or 2 or 5) || value.TopCount is not (3 or 10 or 20) ||
            !double.IsFinite(value.OtherPartsWatts) || value.OtherPartsWatts is < 0 or > 1000 ||
            !double.IsFinite(value.PsuEfficiency) || value.PsuEfficiency is < .5 or > 1 ||
            value.Tariffs is not { Length: > 0 and <= 1000 } ||
            value.Tariffs.Any(x => x is null || x.YuanPerKwh is < 0 or > 1000) ||
            value.TaskbarMetrics is not { Length: > 0 and <= 6 } ||
            value.TaskbarMetrics.Any(x => !Enum.IsDefined(x)) ||
            !Enum.IsDefined(value.OrbMetric) || !Enum.IsDefined(value.SelectedTab) ||
            value.PanelSelectedTab is { } panel && !Enum.IsDefined(panel))
            throw new ArgumentException("设置超出有效范围，请核对刷新间隔、电源效率、补偿功率与电价。");
        return value with
        {
            TaskbarMetrics = value.TaskbarMetrics.Distinct().ToArray(),
            Tariffs = value.Tariffs.OrderBy(x => x.EffectiveFrom).DistinctBy(x => x.EffectiveFrom).ToArray(),
            Theme = Enum.IsDefined(value.Theme) ? value.Theme : ThemePreference.Dark,
            Language = Enum.IsDefined(value.Language) ? value.Language : LanguagePreference.System,
            HistoryChart = Enum.IsDefined(value.HistoryChart) ? value.HistoryChart : HistoryChartStyle.Bars,
            PanelSelectedTab = value.PanelSelectedTab ?? value.SelectedTab,
            OrbLeft = Finite(value.OrbLeft), OrbTop = Finite(value.OrbTop),
            TaskbarLeft = Finite(value.TaskbarLeft), TaskbarTop = Finite(value.TaskbarTop)
        };
        static double? Finite(double? x) => x.HasValue && double.IsFinite(x.Value) ? x : null;
    }

    public OperationResult SaveSettings(AppSettings settings)
    {
        _settingsTimer.Stop();
        var startupChanged = false;
        try
        {
            settings = Validate(settings);
            var old = Settings;
            startupChanged = _persistedSettings.StartWithWindows != settings.StartWithWindows ||
                !_persistedSettings.OnboardingCompleted && settings.OnboardingCompleted;
            if (startupChanged) ApplyStartup(settings.StartWithWindows);
            if (_settingsBackupNeeded && File.Exists(_settingsPath))
            {
                var backup = Path.Combine(Path.GetDirectoryName(_settingsPath)!, $"settings.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
                File.Copy(_settingsPath, backup, false);
                _settingsBackupNeeded = false;
            }
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _settingsPath, overwrite: true);
            Settings = settings;
            _persistedSettings = settings;
            _settingsDirty = false;
            ApplyChanges(old, settings);
            Updated?.Invoke(this, EventArgs.Empty);
            return new(true, "设置已保存。");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (startupChanged)
                try { ApplyStartup(_persistedSettings.StartWithWindows); } catch (Exception rollback) { Trace.TraceError("无法还原启动设置：{0}", rollback.Message); }
            Updated?.Invoke(this, EventArgs.Empty);
            return new(false, "设置保存失败：" + ex.Message);
        }
    }

    public void QueueSettings(Func<AppSettings, AppSettings> update)
    {
        try
        {
            var old = Settings;
            var next = Validate(update(old));
            Settings = next;
            _settingsDirty = true;
            ApplyChanges(old, next);
            _settingsTimer.Stop();
            _settingsTimer.Start();
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Notify(new(false, "设置未应用：" + ex.Message)); }
    }

    public void SelectMetric(MetricKind metric, bool panel)
    {
        if (!Enum.IsDefined(metric)) return;
        if (panel && (Settings.PanelSelectedTab ?? Settings.SelectedTab) == metric || !panel && Settings.SelectedTab == metric) return;
        QueueSettings(current => panel ? current with { PanelSelectedTab = metric } : current with { SelectedTab = metric });
    }

    private void ApplyChanges(AppSettings old, AppSettings settings)
    {
        if (old.Theme != settings.Theme) Ui.ApplyTheme(settings.Theme);
        if (old.Language != settings.Language) Ui.ApplyLanguage(settings.Language);
        if (old.ShowOrb != settings.ShowOrb || old.ShowTaskbar != settings.ShowTaskbar) ApplyWidgets();
        if (old.RefreshSeconds != settings.RefreshSeconds) RestartSampling();
    }

    private void ApplyStartup(bool startWithWindows)
    {
        if (_isolated) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (startWithWindows)
            key.SetValue("WattMonitor", $"\"{Environment.ProcessPath}\" --background", RegistryValueKind.String);
        else key.DeleteValue("WattMonitor", false);
    }

    private void ApplyWidgets()
    {
        if (Settings.ShowOrb)
        {
            _orb ??= new OrbWindow(this);
            UpdateOrbVisibility();
        }
        else _orb?.Hide();
        if (Settings.ShowTaskbar)
        {
            _taskbar ??= new TaskbarWidget(this);
            _taskbar.SetVisible(true);
        }
        else { _taskbar?.SetVisible(false); SetTaskbarMode("已隐藏"); }
    }

    private void UpdateOrbVisibility()
    {
        if (_orb is null) return;
        var mainInFront = _main?.IsActive == true ||
            (_main is not null && Application.Current.Windows.OfType<Window>().Any(window => window.IsActive && window.Owner == _main));
        var show = Settings.ShowOrb && !mainInFront;
        if (show && !_orb.IsVisible) _orb.Show();
        else if (!show && _orb.IsVisible) _orb.Hide();
    }

    private void RefreshStatus()
    {
        var state = MonitorStatus;
        Status = state.SamplingError is not null ? "采集中断 · 可重试" :
            state.StorageError is not null ? "记录保存失败" :
            state.IsStale ? "数据已过期 · 等待重新连接" :
            state.LastSample is null ? "正在连接采集源…" :
            state.Connection + (state.PowerComplete ? " · 正在记录" : " · 功率数据不完整");
        _tray.Text = "WattHalo · " + (state.StorageError is not null ? "记录保存失败" :
            state.SamplingError is not null ? "采集中断" : state.IsStale ? "数据已过期" :
            Snapshot?.EstimatedHostWatts is { } watts ? $"主机估算 {watts:F0} W" : "功率数据不完整");
    }

    public void ResumeMonitoring()
    {
        _samplingError = null;
        RefreshStatus();
        RestartSampling();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void RestartSampling()
    {
        _sampling?.Cancel();
        var previous = _samplingTask;
        var previousCancellation = _sampling;
        _sampling = new CancellationTokenSource();
        var token = _sampling.Token;
        _samplingTask = Task.Run(async () =>
        {
            if (previous is not null)
                try { await previous; } catch (OperationCanceledException) { }
            previousCancellation?.Dispose();
            var queue = Channel.CreateBounded<RecordItem>(new BoundedChannelOptions(120)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
            });
            var writer = queue.Writer;
            var recorder = Task.Run(() => RecordSamples(queue.Reader));
            var resetBaseline = true;
            var retrySeconds = 1;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await foreach (var raw in TelemetryClient.Read(Settings.RefreshSeconds, _isolated, token))
                        {
                            token.ThrowIfCancellationRequested();
                            var settings = Settings;
                            var sample = Estimate(raw, settings);
                            var received = Stopwatch.GetTimestamp();
                            QueuePresentation(p =>
                            {
                                p.Sample = sample; p.Receipt = received;
                                p.SamplingChanged = true; p.SamplingError = null;
                            });
                            if (writer.TryWrite(new(sample, settings, resetBaseline, Interlocked.Read(ref _overflowRevision)))) resetBaseline = false;
                            else
                            {
                                resetBaseline = true;
                                Interlocked.Increment(ref _overflowRevision);
                                QueuePresentation(p => { p.StorageChanged = true; p.StorageError = "记录处理延迟，部分时段未记录"; });
                            }
                            retrySeconds = 1;
                        }
                        if (!token.IsCancellationRequested) throw new IOException("采集连接已结束");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        resetBaseline = true;
                        QueuePresentation(p => { p.SamplingChanged = true; p.SamplingError = "采集中断：" + ex.Message; });
                        await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token);
                        retrySeconds = Math.Min(30, retrySeconds * 2);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                writer.TryComplete();
                await recorder;
            }
        });
    }

    private async Task RecordSamples(ChannelReader<RecordItem> reader)
    {
        long lastTotals = 0;
        var failed = false;
        await foreach (var item in reader.ReadAllAsync())
        {
            EnergyTotals? today = null, week = null, month = null;
            string? error = null;
            try
            {
                lock (_storeGate)
                {
                    if (item.ResetBaseline || failed) _store.ResetBaseline();
                    _store.Record(item.Sample, item.Settings);
                    if (lastTotals == 0 || Stopwatch.GetElapsedTime(lastTotals).TotalSeconds >= 30)
                    {
                        var local = DateTimeOffset.Now;
                        today = _store.GetTotals(LocalBoundary(local.Date), local);
                        week = _store.GetTotals(LocalBoundary(local.Date.AddDays(-((int)local.DayOfWeek + 6) % 7)), local);
                        month = _store.GetTotals(LocalBoundary(new DateTime(local.Year, local.Month, 1)), local);
                        lastTotals = Stopwatch.GetTimestamp();
                    }
                }
                failed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                failed = true;
                error = "历史记录失败：" + ex.Message;
            }
            if (error is null && item.OverflowRevision != Interlocked.Read(ref _overflowRevision))
                error = "记录处理延迟，部分时段未记录";
            QueuePresentation(p =>
            {
                p.StorageChanged = true; p.StorageError = error;
                if (today is not null) { p.Today = today; p.Week = week; p.Month = month; }
            });
        }
    }

    private void QueuePresentation(Action<PendingPresentation> update)
    {
        lock (_presentationGate)
        {
            update(_pendingPresentation);
            if (_presentationQueued) return;
            _presentationQueued = true;
        }
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(DrainPresentation, DispatcherPriority.Background);
    }

    private void DrainPresentation()
    {
        PendingPresentation pending;
        lock (_presentationGate)
        {
            pending = _pendingPresentation;
            _pendingPresentation = new();
            _presentationQueued = false;
        }
        if (_exiting) return;
        var failedBefore = _storageFailed;
        if (pending.StorageChanged)
        {
            _storageError = pending.StorageError;
            _storageFailed = pending.StorageError is not null;
        }
        if (pending.SamplingChanged) _samplingError = pending.SamplingError;
        if (pending.Sample is { } sample)
        {
            Snapshot = sample;
            _lastReceipt = pending.Receipt;
            var threshold = Math.Max(5, 3 * Settings.RefreshSeconds);
            if (_staleLatched && Stopwatch.GetElapsedTime(pending.Receipt).TotalSeconds <= threshold &&
                (DateTimeOffset.UtcNow - sample.Timestamp).TotalSeconds <= threshold && ++_freshAfterStale >= 2)
            {
                _staleLatched = false;
                _freshAfterStale = 0;
            }
            _trend.Enqueue(sample with { Processes = [] });
            while (_trend.TryPeek(out var first) && sample.Timestamp - first.Timestamp > TimeSpan.FromHours(1)) _trend.Dequeue();
        }
        if (pending.Today is not null) { Today = pending.Today; Week = pending.Week!; Month = pending.Month!; }
        RefreshStatus();
        if (!failedBefore && _storageError is not null) Notify(new(false, _storageError));
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private static DateTimeOffset LocalBoundary(DateTime date) => new(date, TimeZoneInfo.Local.GetUtcOffset(date));

    internal static TelemetrySnapshot Estimate(TelemetrySnapshot sample, AppSettings settings)
    {
        var discrete = sample.Gpus.Where(g => g.IsDiscrete).ToArray();
        // Values above one gigawatt cannot be meaningful desktop sensor readings and can overflow cost arithmetic.
        static bool ValidPower(double? value) => value is { } watts && double.IsFinite(watts) && watts is >= 0 and <= 1_000_000_000;
        var complete = sample.GpuEnumerationComplete && ValidPower(sample.CpuPackageWatts) && discrete.All(g => ValidPower(g.PowerWatts));
        var validValues = discrete.Select(g => g.PowerWatts).Append(sample.CpuPackageWatts)
            .Where(ValidPower).Select(x => x!.Value).ToArray();
        double? component = validValues.Length == 0 ? null : validValues.Sum();
        return sample with
        {
            ComponentWatts = component,
            PowerComplete = complete,
            EstimatedHostWatts = complete && component.HasValue
                ? (component.Value + settings.OtherPartsWatts) / settings.PsuEfficiency : null
        };
    }

    public IReadOnlyList<HistoryPoint> QueryHistory(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_storeGate) return _store.GetHistory(from, to);
    }

    public Task<IReadOnlyList<HistoryPoint>> QueryHistoryAsync(DateTimeOffset from, DateTimeOffset to) =>
        Task.Run(() => QueryHistory(from, to));

    internal Task<(IReadOnlyList<HistoryPoint> Points, IReadOnlyList<CoverageSummary> Coverage)>
        QueryHistoryWithCoverageAsync(DateTimeOffset from, DateTimeOffset to,
            IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> ranges) => Task.Run(() =>
    {
        lock (_storeGate) return (_store.GetHistory(from, to), _store.GetCoverageBatch(ranges));
    });

    public async Task<OperationResult> ExportHistoryAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 表格|*.csv", FileName = $"WattHalo-{from:yyyyMMdd}-{to.AddTicks(-1):yyyyMMdd}.csv" };
        if (dialog.ShowDialog() != true) return new(false, "已取消导出。", true);
        try
        {
            var destination = Path.GetFullPath(dialog.FileName);
            var dataDirectory = Path.GetDirectoryName(_settingsPath)!;
            var protectedFiles = new[] { "settings.json", "settings.json.tmp", "history.db", "history.db-wal", "history.db-shm" };
            if (protectedFiles.Any(file => string.Equals(destination, Path.Combine(dataDirectory, file), StringComparison.OrdinalIgnoreCase)))
                return new(false, "导出位置与软件数据文件冲突，请选择其他文件名。");
            await Task.Run(() => { lock (_storeGate) _store.ExportCsv(destination, from, to); });
            return new(true, "用电记录已导出。", Path: destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { return new(false, "导出失败：" + ex.Message); }
    }

    public string CloseProcess(ProcessSample process, bool force) => ProcessActions.Close(process, force);

    public void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow(this);
            _main.Activated += (_, _) => UpdateOrbVisibility();
            _main.Deactivated += (_, _) => _dispatcher.BeginInvoke(UpdateOrbVisibility);
            _main.StateChanged += (_, _) =>
            {
                if (_main.IsVisible && _main.WindowState != WindowState.Minimized)
                    _dispatcher.BeginInvoke(DeliverPendingNotice, DispatcherPriority.Loaded);
            };
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _dispatcher.BeginInvoke(DeliverPendingNotice, DispatcherPriority.Loaded);
    }

    public void ShowMain(MetricKind? metric)
    {
        ShowMain();
        if (metric.HasValue) _main?.NavigateTo("实时监测", metric);
    }

    public void TogglePanel() => TogglePanel(false);

    public void TogglePanel(bool animate)
    {
        if (_panel is null)
        {
            _panel = new PanelWindow(this);
            _panel.Activated += (_, _) => UpdateOrbVisibility();
            _panel.Deactivated += (_, _) => _dispatcher.BeginInvoke(UpdateOrbVisibility);
        }
        if (_panel.IsVisible) _panel.Dismiss(!animate);
        else { _panel.ShowNear(Settings.ShowOrb ? _orb : null, animate); _panel.Activate(); _dispatcher.BeginInvoke(DeliverPendingNotice, DispatcherPriority.Loaded); }
        UpdateOrbVisibility();
    }

    public void PanelVisibilityChanged() => UpdateOrbVisibility();

    internal void SetTaskbarMode(string mode)
    {
        if (TaskbarMode == mode) return;
        TaskbarMode = mode;
        Updated?.Invoke(this, EventArgs.Empty);
    }

    internal void RetryTaskbar()
    {
        if (!Settings.ShowTaskbar) return;
        _taskbar ??= new TaskbarWidget(this);
        _taskbar.SetVisible(true);
        _taskbar.Retry();
    }

    public async void InstallHardwareSupport()
    {
        if (RepairingService) return;
        var installer = Path.Combine(AppContext.BaseDirectory, "Install.ps1");
        if (!File.Exists(installer))
        {
            Notify(new(false, "请先生成发行包，再运行其中的安装程序。"));
            return;
        }
        RepairingService = true;
        Updated?.Invoke(this, EventArgs.Empty);
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            using var installation = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WattMonitor");
            var installDirectory = installation?.GetValue("InstallDirectory") as string ?? @"D:\Program Files\WattMonitor";
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", installer,
                "-OwnerSid", sid, "-InstallDirectory", installDirectory, "-RepairService", "-Quiet" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) { Notify(new(false, Ui.T("无法启动采集服务修复程序。", "Could not start the sampling-service repair."))); return; }
            Notify(new(true, Ui.T("采集服务修复程序已启动。", "Sampling-service repair started.")));
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) { ResumeMonitoring(); Notify(new(true, Ui.T("采集服务修复完成，正在重新连接。", "Sampling-service repair completed; reconnecting."))); }
            else Notify(new(false, Ui.T("采集服务修复未完成。请确认 Windows 服务中的 WattMonitor 未被禁用，然后重试。", "Sampling-service repair did not complete. Check that WattMonitor is enabled in Windows Services, then retry.")));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Notify(new(false, Ui.T("采集服务修复未完成：", "Sampling-service repair did not complete: ") + ex.Message)); }
        finally { RepairingService = false; Updated?.Invoke(this, EventArgs.Empty); }
    }

    public async void Quit()
    {
        if (_exiting) return;
        if (_main is not null && !_main.ConfirmBeforeExit()) return;
        if (_settingsDirty)
        {
            var result = SaveSettings(Settings);
            if (!result.Success && Ui.Confirm(result.Message + "\n仍要退出吗？", "设置尚未保存", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        _exiting = true;
        _sampling?.Cancel();
        if (_samplingTask is not null)
            try { await _samplingTask; } catch (OperationCanceledException) { }
        try { lock (_storeGate) _store.Flush(); }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (Ui.Confirm("最后一批记录未能保存：" + ex.Message + "\n仍然退出可能丢失尚未保存的采样。是否退出？", "无法保存记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                _exiting = false;
                RestartSampling();
                return;
            }
        }
        _tray.Visible = false;
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _exiting = true;
        _settingsTimer.Stop();
        _statusTimer.Stop();
        _updateTimer.Stop();
        if (_settingsDirty)
        {
            var result = SaveSettings(Settings);
            if (!result.Success) Trace.TraceError("退出时设置保存失败：{0}", result.Message);
        }
        _sampling?.Cancel();
        _taskbar?.Dispose();
        _updates.Changed -= UpdatesChanged;
        _updates.Dispose();
        Ui.ThemeChanged -= TrayThemeChanged;
        Ui.LanguageChanged -= TrayLanguageChanged;
        _tray.Dispose();
        _trayMenu.Dispose();
        try { lock (_storeGate) _store.Dispose(); }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        { Trace.TraceError("退出时历史存储关闭失败：{0}", ex.Message); }
        _sampling?.Dispose();
    }
}
