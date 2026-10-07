using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DownloadLimit.Core;
using DownloadLimit.Windows;
using Microsoft.Win32;

namespace DownloadLimit.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _enable = new("Enable");
    private readonly ToolStripMenuItem _limits = new("Set Upload/Download Limits…");
    private readonly ToolStripMenuItem _bypass = new("Bypass for 15 minutes");
    private readonly ToolStripMenuItem _resume = new("Resume limits now");
    private readonly ToolStripMenuItem _startup = new("Start with Windows");
    private readonly ToolStripMenuItem _exit = new("Exit");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly BypassController _state = new();
    private readonly IMonotonicClock _clock = new SystemMonotonicClock();
    private readonly Icon _offIcon = MakeIcon(Color.DimGray);
    private readonly Icon _onIcon = MakeIcon(Color.ForestGreen);
    private readonly Icon _bypassIcon = MakeIcon(Color.DodgerBlue);
    private readonly Icon _errorIcon = MakeIcon(Color.Firebrick);
    private AppSettings _settings;
    private NetworkClassifier _classifier = new();
    private volatile ShapingEngine? _engine;
    private SpeedMonitor? _monitor;
    private LimitDialog? _limitDialog;
    private long _monitorGeneration;
    private long _sampleTime;
    private (long Download, long Upload) _previousTotals;
    private double _downloadSpeed, _uploadSpeed;
    private int _refreshRequested;
    private bool _busy, _exiting, _monitorErrorHandled;
    private volatile bool _suspended;

    public TrayContext(bool firstInstall)
    {
        _settings = AppFiles.LoadSettings(out string? warning);
        _dispatcher.CreateControl();
        _menu.Items.AddRange([_enable, _limits, _bypass, _resume, new ToolStripSeparator(),
            _startup, new ToolStripSeparator(), _exit]);
        _tray = new NotifyIcon { Icon = _offIcon, ContextMenuStrip = _menu,
            Text = "DownloadLimit | Limits off", Visible = true };
        _enable.Click += (_, _) => Change(ToggleAsync);
        _limits.Click += (_, _) => ShowLimits();
        _bypass.Click += (_, _) => Change(BypassAsync);
        _resume.Click += (_, _) => Change(ResumeLimitsAsync);
        _startup.Click += (_, _) => Change(ToggleStartupAsync);
        _exit.Click += (_, _) => RequestExit();
        _tray.DoubleClick += (_, _) => ShowLimits();
        _timer.Tick += Tick;
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionEnding += SessionEnding;
        Render();
        _timer.Start();
        Change(async () =>
        {
            (bool Enabled, string? Error) startup = await Task.Run(() =>
            {
                try
                {
                    bool exists = StartupTask.Exists();
                    if (_settings.StartWithWindows && !exists) StartupTask.SetEnabled(true, AppFiles.InstalledExecutable);
                    else if (!_settings.StartWithWindows && exists) StartupTask.SetEnabled(false, AppFiles.InstalledExecutable);
                    return (StartupTask.Exists(), (string?)null);
                }
                catch (Exception error)
                {
                    AppLog.Error(error);
                    bool exists;
                    try { exists = StartupTask.Exists(); } catch { exists = false; }
                    return (exists, error.Message);
                }
            });
            _settings = _settings with { StartWithWindows = startup.Enabled };
            AppFiles.SaveSettings(_settings);
            await RefreshAsync();
            if (startup.Error is not null) Notify(startup.Error, ToolTipIcon.Warning);
            else if (warning is not null) Notify(warning, ToolTipIcon.Warning);
            else if (firstInstall) Notify("Ready in the system tray. Limits are off; right-click the icon to Enable.");
        });
    }

    private async void Change(Func<Task> action, bool exitingAction = false)
    {
        await _transition.WaitAsync();
        try
        {
            if (_exiting && !exitingAction) return;
            _busy = true;
            Render();
            await action();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
            _state.Fault();
            try { await StopEngineAsync(); } catch (Exception cleanup) { AppLog.Error(cleanup); }
            // A broken monitor never keeps a diverting handle alive.
            if (_monitor?.Error is not null) await StopMonitorAsync();
            if (!_exiting) Notify(ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            if (!_exiting) Render();
            _transition.Release();
        }
    }

    private async Task ToggleAsync()
    {
        if (_state.Mode is RunMode.Enabled or RunMode.Bypassing)
        {
            _state.Disable();
            await StopEngineAsync();
        }
        else
        {
            if (_monitor is null || _monitor.Error is not null) await RefreshAsync();
            if (_suspended) return;
            StartEngine();
            _state.Enable();
        }
    }

    private void StartEngine()
    {
        if (_engine is not null || _suspended || _exiting) return;
        var transport = new WinDivertTransport(_classifier.BuildFilter(true));
        ShapingEngine? created = null;
        try
        {
            created = new ShapingEngine(transport, _clock, _classifier, _settings,
                onFault: error =>
                {
                    AppLog.Error(error);
                    Post(() => Change(async () =>
                    {
                        if (!ReferenceEquals(_engine, created)) return;
                        _state.Fault();
                        await StopEngineAsync();
                        Notify(error.Message, ToolTipIcon.Error);
                    }));
                });
            _engine = created;
            if (created.IsStopping) throw new IOException("The shaping engine stopped during initialization. Networking remains unrestricted.");
        }
        catch
        {
            if (created is null) transport.Dispose();
            else _ = created.StopAsync();
            throw;
        }
    }

    private async Task StopEngineAsync()
    {
        ShapingEngine? engine = _engine;
        _engine = null;
        if (engine is not null) await engine.StopAsync();
    }

    private async Task StopMonitorAsync()
    {
        SpeedMonitor? monitor = _monitor;
        _monitor = null;
        if (monitor is not null) await monitor.DisposeAsync();
    }

    private async Task RefreshAsync()
    {
        await StopEngineAsync();
        await StopMonitorAsync();
        if (_suspended || _exiting) return;
        _classifier = await Task.Run(LocalNetworks.Snapshot);
        _monitor = new SpeedMonitor(_classifier);
        _monitorGeneration++;
        _previousTotals = _monitor.Totals;
        _sampleTime = Stopwatch.GetTimestamp();
        _downloadSpeed = _uploadSpeed = 0;
        _monitorErrorHandled = false;
        _state.TryExpire(NowMilliseconds);
        if (_state.Mode == RunMode.Enabled) StartEngine();
    }

    private void ShowLimits()
    {
        if (_busy || _suspended || _exiting || _limitDialog is not null) return;
        using var dialog = new LimitDialog(_settings);
        _limitDialog = dialog;
        Render();
        try
        {
            // Modal UI runs its own message loop, but must not hold the state-transition
            // semaphore: bypass expiry, Disable and Exit must still operate.
            if (dialog.ShowDialog() == DialogResult.OK && !_exiting)
            {
                decimal download = dialog.DownloadMbps, upload = dialog.UploadMbps;
                Change(() =>
                {
                    AppSettings proposed = _settings with { DownloadMbps = download, UploadMbps = upload };
                    AppFiles.SaveSettings(proposed);
                    _settings = proposed;
                    _engine?.UpdateLimits(_settings);
                    return Task.CompletedTask;
                });
            }
        }
        finally { _limitDialog = null; if (!_exiting) Render(); }
    }

    private async Task BypassAsync()
    {
        if (!_state.BeginBypass(NowMilliseconds)) return;
        await StopEngineAsync();
    }

    private Task ResumeLimitsAsync()
    {
        if (_state.Mode != RunMode.Bypassing || _suspended) return Task.CompletedTask;
        StartEngine();
        _state.Enable();
        return Task.CompletedTask;
    }

    private async Task ToggleStartupAsync()
    {
        bool desired = !_settings.StartWithWindows;
        bool previous = _settings.StartWithWindows;
        await Task.Run(() => StartupTask.SetEnabled(desired, AppFiles.InstalledExecutable));
        try
        {
            var proposed = _settings with { StartWithWindows = desired };
            AppFiles.SaveSettings(proposed);
            _settings = proposed;
        }
        catch
        {
            await Task.Run(() => StartupTask.SetEnabled(previous, AppFiles.InstalledExecutable));
            throw;
        }
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (_exiting) return;
        if (_monitor is { Error: null } monitor)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - _sampleTime) / (double)Stopwatch.Frequency;
            var totals = monitor.Totals;
            if (elapsed > 0)
            {
                _downloadSpeed = Math.Max(0, totals.Download - _previousTotals.Download) * 8 / elapsed / 1_000_000;
                _uploadSpeed = Math.Max(0, totals.Upload - _previousTotals.Upload) * 8 / elapsed / 1_000_000;
            }
            _previousTotals = totals;
            _sampleTime = now;
        }
        if (!_busy && !_suspended)
        {
            if (_monitor?.Error is Exception error && !_monitorErrorHandled)
            {
                _monitorErrorHandled = true;
                long generation = _monitorGeneration;
                Change(async () =>
                {
                    if (generation != _monitorGeneration) return;
                    _state.Fault();
                    await StopEngineAsync();
                    await StopMonitorAsync();
                    AppLog.Error(error);
                    Notify(error.Message, ToolTipIcon.Error);
                });
            }
            else if (Interlocked.Exchange(ref _refreshRequested, 0) != 0) Change(RefreshAsync);
            else if (_state.Mode == RunMode.Bypassing && _state.RemainingMilliseconds(NowMilliseconds) == 0)
                Change(() =>
                {
                    if (_state.TryExpire(NowMilliseconds)) StartEngine();
                    return Task.CompletedTask;
                });
        }
        Render();
    }

    private void Render()
    {
        RunMode mode = _state.Mode;
        bool active = mode is RunMode.Enabled or RunMode.Bypassing;
        _enable.Text = active ? "Disable" : "Enable";
        _enable.Checked = active;
        _enable.Enabled = _startup.Enabled = !_busy && !_suspended;
        _limits.Enabled = !_busy && !_suspended && _limitDialog is null;
        _bypass.Enabled = !_busy && !_suspended && active;
        _bypass.Checked = mode == RunMode.Bypassing;
        _resume.Visible = mode == RunMode.Bypassing;
        _resume.Enabled = !_busy && !_suspended;
        _startup.Checked = _settings.StartWithWindows;
        _tray.Icon = mode switch { RunMode.Enabled => _onIcon, RunMode.Bypassing => _bypassIcon,
            RunMode.Faulted => _errorIcon, _ => _offIcon };
        string status = _suspended ? "Suspended" : mode switch
        {
            RunMode.Enabled => "On", RunMode.Disabled => "Off", RunMode.Faulted => "Error",
            _ => $"Bypass {TimeSpan.FromMilliseconds(_state.RemainingMilliseconds(NowMilliseconds)):mm\\:ss}"
        };
        bool available = _monitor is { Error: null };
        string download = available ? _downloadSpeed.ToString("0.0", CultureInfo.InvariantCulture) : "?";
        string upload = available ? _uploadSpeed.ToString("0.0", CultureInfo.InvariantCulture) : "?";
        string text = $"D {download}/{_settings.DownloadMbps:G} U {upload}/{_settings.UploadMbps:G} Mbps | {status}";
        _tray.Text = text.Length <= 63 ? text : text[..63];
    }

    private static long NowMilliseconds => checked((long)GetTickCount64());

    private void NetworkChanged(object? sender, EventArgs args) => Interlocked.Exchange(ref _refreshRequested, 1);

    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend)
        {
            _suspended = true;
            _ = _engine?.StopAsync();
            Post(() => Change(async () => { await StopEngineAsync(); await StopMonitorAsync(); }));
        }
        else if (args.Mode == PowerModes.Resume)
            Post(() => Change(async () => { _suspended = false; await RefreshAsync(); }));
    }

    private void SessionEnding(object sender, SessionEndingEventArgs args)
    {
        _ = _engine?.StopAsync();
        Post(RequestExit);
    }

    private void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        _state.Disable();
        _timer.Stop();
        if (_limitDialog is not null) { _limitDialog.DialogResult = DialogResult.Cancel; _limitDialog.Close(); }
        // Stop capture before waiting behind an unrelated startup/settings operation.
        _ = _engine?.StopAsync();
        Change(async () =>
        {
            try { await StopEngineAsync(); await StopMonitorAsync(); }
            finally { _tray.Visible = false; ExitThread(); }
        }, exitingAction: true);
    }

    public void HandleUnexpectedError(Exception error)
    {
        _ = _engine?.StopAsync();
        Post(() => Change(async () =>
        {
            _state.Fault();
            await StopEngineAsync();
            AppLog.Error(error);
            Notify(error.Message, ToolTipIcon.Error);
        }));
    }

    private void Post(Action action)
    {
        if (_exiting || _dispatcher.IsDisposed) return;
        try { _dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void Notify(string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (!_exiting) _tray.ShowBalloonTip(6000, "DownloadLimit", message, icon);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.SessionEnding -= SessionEnding;
            _timer.Stop();
            _engine?.StopAsync().GetAwaiter().GetResult();
            _monitor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _dispatcher.Dispose();
            _offIcon.Dispose(); _onIcon.Dispose(); _bypassIcon.Dispose(); _errorIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon MakeIcon(Color color)
    {
        using var image = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            using var pen = new Pen(Color.White, 2.3f);
            graphics.FillEllipse(fill, 1, 1, 30, 30);
            graphics.DrawLines(pen, [new(10, 9), new(10, 23), new(6, 19)]);
            graphics.DrawLine(pen, 10, 23, 14, 19);
            graphics.DrawLines(pen, [new(22, 23), new(22, 9), new(18, 13)]);
            graphics.DrawLine(pen, 22, 9, 26, 13);
        }
        nint handle = image.GetHicon();
        try { using Icon borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
