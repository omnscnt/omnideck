using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Omnideck.Core;
using Omnideck.Core.Hotkeys;
using Omnideck.Core.Devices;
using Omnideck.Core.Media;
using Omnideck.Osd;
using Omnideck.Services;
using Omnideck.Taskbar;
using Omnideck.ViewModels;
using Omnideck.Views;

namespace Omnideck;

public partial class App : Application
{
    private const string RestartArgument = "--restart";

    private Mutex? _singleInstance;
    private AudioService? _audio;
    private HotkeyEngine? _hotkeys;
    private HotkeyActionRunner? _runner;
    private AppSettings _settings = new();
    private TrayIconHost? _tray;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private NowPlayingWatcher? _nowPlaying;
    private TaskbarWidget? _widget;
    private VolumeOsd? _osd;
    private VolumeOsdFeed? _osdFeed;
    private OsdNoticeFeed? _notices;
    private Timer? _updateTimer;
    private BatteryMonitor? _batteries;
    private LowBatteryNotifier? _lowBattery;

    public static App Instance => (App)Current;

    /// <summary>The sound core, shared by the mixer and hotkeys.</summary>
    public static AudioService Audio => Instance._audio!;

    public App()
    {
        InitializeComponent();
        // Crashes leave nothing in the event log for a WinUI app: write them down ourselves.
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("UI thread", e.Exception);
            e.Handled = true; // a tray utility should survive a failed click rather than vanish
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("background thread", e.ExceptionObject as Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length == 5 && cmdArgs[1] == Updater.PrepareArgument &&
            int.TryParse(cmdArgs[2], out int callerId) && Guid.TryParseExact(cmdArgs[3], "N", out Guid operation) &&
            Version.TryParse(cmdArgs[4], out Version? updateVersion))
        {
            try { await Updater.PrepareProtectedAsync(callerId, operation, updateVersion); }
            catch (Exception ex)
            {
                CrashLog.Write("prepare protected update", ex);
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }

        if (cmdArgs.Length == 4 && cmdArgs[1] == AdministratorTask.ConfigureArgument && bool.TryParse(cmdArgs[2], out bool enabled))
        {
            try { AdministratorTask.Configure(enabled, cmdArgs[3]); }
            catch (Exception ex)
            {
                CrashLog.Write("configure administrator task", ex);
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }

        bool fromAutostart = cmdArgs.Contains(AutoStart.LaunchArgument) || cmdArgs.Contains(Updater.UpdatedArgument);
        bool scheduledLaunch = cmdArgs.Contains(AdministratorTask.LaunchArgument);
        bool restarting = cmdArgs.Contains(RestartArgument);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnideck.ShowFlyout");
        var menuSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnideck.ShowMenu");
        var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnideck.ShowSettings");
        using var taskReady = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnideck.AdministratorTaskReady");
        EventWaitHandle activationSignal = cmdArgs.Contains("--menu") ? menuSignal : cmdArgs.Contains("--settings") ? settingsSignal : showSignal;
        _singleInstance = new Mutex(true, @"Local\Omnideck.SingleInstance", out bool isFirst);
        if (!isFirst && (scheduledLaunch || restarting))
        {
            // The original instance releases the mutex after shutting down its tray and hotkeys.
            isFirst = TakeInstanceMutex(TimeSpan.FromSeconds(10));

            if (!isFirst)
            {
                CrashLog.Write("restart", new TimeoutException("The previous instance did not exit."));
                Exit();
                return;
            }
        }

        if (!isFirst)
        {
            // Launching the exe again pops the running instance's mixer (--menu: tray menu, --settings: settings).
            if (!cmdArgs.Contains(AutoStart.LaunchArgument))
            {
                AllowSetForegroundWindow(-1); // Hand our foreground right to the running instance.
                activationSignal.Set();
            }
            Exit();
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        bool isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        bool scheduledLaunchFailed = false;
        if (scheduledLaunch) taskReady.Set();
        if (!isAdministrator && !scheduledLaunch && AdministratorTask.TryRun())
        {
            if (!fromAutostart) activationSignal.Set();
            _singleInstance.ReleaseMutex();
            if (taskReady.WaitOne(TimeSpan.FromSeconds(10)) || !TakeInstanceMutex(TimeSpan.Zero))
            {
                // Keep the activation events available until the elevated instance has opened them.
                GC.KeepAlive(showSignal);
                GC.KeepAlive(menuSignal);
                GC.KeepAlive(settingsSignal);
                Exit();
                return;
            }

            scheduledLaunchFailed = true;
            if (!fromAutostart) activationSignal.Reset();
        }

        _settings = SettingsStore.Load();
        // Tray app: hiding the flyout must not end the process.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Core Audio session notifications want an MTA thread; the objects themselves are free-threaded.
        var appVolumePath = Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "app-volumes.json");
        _audio = await Task.Run(() => new AudioService(appVolumePath));

        VolumeCurve.Logarithmic = _settings.LogarithmicVolume;
        InstallLayout.RenameLegacyLauncher(); // before autostart: it points at the launcher
        AutoStart.Migrate();
        var media = new MediaSessionService();
        _runner = new HotkeyActionRunner(_audio, media);
        _hotkeys = new HotkeyEngine();
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _hotkeys.Triggered += binding =>
        {
            // App-level actions (the mixer) stay out of the sound core.
            if (binding.Action == HotkeyAction.OpenMixer) dispatcher.TryEnqueue(() => _flyout?.Toggle());
            else _ = _runner.RunAsync(binding);
        };
        _hotkeys.SetBindings(_settings.Hotkeys);

        _flyout = new FlyoutWindow(new MixerViewModel(_audio, dispatcher, _settings.MixerExpanded, expanded =>
        {
            _settings.MixerExpanded = expanded;
            SettingsStore.Save(_settings);
        }));
        ApplyMixerLayout();
        _tray = new TrayIconHost(() => _flyout.Toggle(), ShowSettings, Shutdown);
        if (scheduledLaunchFailed) _tray.Notify("omnideck.", "The administrator task did not start. Omnideck is running with regular permissions.");

        _nowPlaying = new NowPlayingWatcher(media);
        _nowPlaying.Filter = _settings.TaskbarWidget.ToFilter();
        _widget = new TaskbarWidget(_nowPlaying);
        _widget.Configure(_settings.TaskbarWidget);
        _ = _nowPlaying.StartAsync();

        _osd = new VolumeOsd();
        _osd.Configure(_settings.VolumeOsd);
        _osdFeed = new VolumeOsdFeed(_audio, _runner, _osd, () => _settings.VolumeOsd, media, () => _widget?.VisibleAppName);
        _notices = new OsdNoticeFeed(_audio, _hotkeys, _osd, () => _settings.VolumeOsd);

        _lowBattery = new LowBatteryNotifier((title, text) => dispatcher.TryEnqueue(() => _tray?.Notify(title, text)));
        _batteries = new BatteryMonitor();
        _batteries.Changed += devices =>
        {
            _lowBattery.Check(Shown(devices), _settings.Devices);
            BatteriesChanged?.Invoke(devices);
            dispatcher.TryEnqueue(() => ShowBatteries(devices));
        };
        ApplyDevices();

        new Thread(() =>
        {
            var signals = new WaitHandle[] { showSignal, menuSignal, settingsSignal };
            while (true)
            {
                switch (WaitHandle.WaitAny(signals))
                {
                    case 0: dispatcher.TryEnqueue(() => _flyout.Show()); break;
                    case 1: dispatcher.TryEnqueue(() => _tray.ShowMenu()); break;
                    default: dispatcher.TryEnqueue(ShowSettings); break;
                }
            }
        }) { IsBackground = true, Name = "Omnideck instance signals" }.Start();

        bool updated = cmdArgs.Contains(Updater.UpdatedArgument);
        if (updated) _tray.Notify("omnideck. updated", $"Version {Updater.CurrentText} is installed.");
        if (updated) _ = Task.Run(InstallLayout.RemoveFlatLeftovers); // first start after leaving the flat layout
        StartUpdateChecks(dispatcher);
        if (!fromAutostart && !scheduledLaunch) _flyout.Show(); // Manual launch: show that we're alive.
        else _flyout.Prewarm(); // tray-only start: warm the window up so the first open is clean
    }

    /// <summary>Opens (or brings forward) the settings window.</summary>
    public void ShowSettings()
    {
        try
        {
            if (_settingsWindow == null)
            {
                var hotkeys = new HotkeysViewModel(_settings.Hotkeys, ApplyHotkeys, _hotkeys!, CurrentAppNames);
                _settingsWindow = new SettingsWindow(hotkeys);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            // Reached from dispatcher callbacks, which UnhandledException doesn't cover: log, don't die.
            CrashLog.Write("settings window", ex);
            _settingsWindow = null;
        }
    }

    private IEnumerable<string> CurrentAppNames() =>
        _audio!.Apps.Where(a => !a.IsSystemSounds && a.ProcessName.Length > 0).Select(a => a.ProcessName);

    private void ApplyHotkeys(IReadOnlyList<HotkeyBinding> bindings)
    {
        _settings.Hotkeys = bindings.ToList();
        _hotkeys!.SetBindings(_settings.Hotkeys);
        SettingsStore.Save(_settings);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>A copy of the widget settings, for the settings window to edit.</summary>
    public TaskbarWidgetSettings TaskbarWidgetSettings => _settings.TaskbarWidget.Clone();

    public void ApplyTaskbarWidget(TaskbarWidgetSettings settings)
    {
        _settings.TaskbarWidget = settings.Clone();
        if (_nowPlaying != null) _nowPlaying.Filter = settings.ToFilter();
        _widget?.Configure(settings);
        SettingsStore.Save(_settings);
    }

    /// <summary>The exe of a running app by name ("spotify"), from the mixer or the process list.</summary>
    public string? FindExePath(string appName)
    {
        var audioApp = _audio?.Apps.FirstOrDefault(a => string.Equals(a.ProcessName, appName, StringComparison.OrdinalIgnoreCase));
        if (audioApp?.ExecutablePath != null) return audioApp.ExecutablePath;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(appName))
        {
            using (process)
                if (ProcessInfo.GetExecutablePath(process.Id) is { } path) return path;
        }
        return null;
    }

    /// <summary>Apps that have a media session or play sound right now, for the widget's app lists.</summary>
    public IEnumerable<string> MediaAppNames() =>
        (_nowPlaying?.SessionAppNames() ?? []).Concat(CurrentAppNames()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n);

    /// <summary>A copy of the volume indicator settings, for the settings window to edit.</summary>
    public VolumeOsdSettings VolumeOsdSettings => _settings.VolumeOsd.Clone();

    public void ApplyVolumeOsd(VolumeOsdSettings settings)
    {
        _settings.VolumeOsd = settings.Clone();
        _osd?.Configure(settings);
        SettingsStore.Save(_settings);
    }

    public void PreviewVolumeOsd() => _osdFeed?.Preview();

    /// <summary>The volume curve for app sliders, hotkey steps and the indicator; switching re-reads the mixer.</summary>
    public bool LogarithmicVolume
    {
        get => _settings.LogarithmicVolume;
        set
        {
            _settings.LogarithmicVolume = value;
            VolumeCurve.Logarithmic = value;
            SettingsStore.Save(_settings);
            if (_flyout != null) foreach (var app in _flyout.ViewModel.Apps) app.Resync();
        }
    }

    // ---- mixer --------------------------------------------------------------------------------------

    public bool AppOutputs
    {
        get => _settings.AppOutputs;
        set { _settings.AppOutputs = value; SaveMixerLayout(); }
    }

    public int ExpandedOutputWidth
    {
        get => _settings.ExpandedOutputWidth;
        set { _settings.ExpandedOutputWidth = value; SaveMixerLayout(); }
    }

    public bool FullDeviceNames
    {
        get => _settings.FullDeviceNames;
        set { _settings.FullDeviceNames = value; SaveMixerLayout(); }
    }

    private void SaveMixerLayout()
    {
        SettingsStore.Save(_settings);
        ApplyMixerLayout();
    }

    private void ApplyMixerLayout() =>
        _flyout?.ViewModel.Configure(_settings.AppOutputs && MixerViewModel.CanRouteApps, _settings.ExpandedOutputWidth, _settings.FullDeviceNames);

    // ---- devices: wireless batteries ----------------------------------------------------------------

    public DeviceSettings DeviceSettings => _settings.Devices.Clone();

    /// <summary>What the last read found (none while reading is off).</summary>
    public IReadOnlyList<DeviceBattery> Batteries => _batteries?.Devices ?? [];

    /// <summary>Raised with each new read, on a thread-pool thread.</summary>
    public event Action<IReadOnlyList<DeviceBattery>>? BatteriesChanged;

    public void ApplyDeviceSettings(DeviceSettings settings)
    {
        _settings.Devices = settings.Clone();
        SettingsStore.Save(_settings);
        ApplyDevices();
    }

    private void ApplyDevices()
    {
        if (_batteries == null) return;
        var settings = _settings.Devices;
        if (settings.Enabled)
            _batteries.Start(TimeSpan.FromMinutes(Math.Clamp(settings.PollMinutes, DeviceSettings.MinPoll, DeviceSettings.MaxPoll)));
        else
            _batteries.Stop();
        ShowBatteries(_batteries.Devices);
    }

    /// <summary>The devices the user didn't hide: what the plaque shows and the low battery notice watches.</summary>
    private List<DeviceBattery> Shown(IReadOnlyList<DeviceBattery> devices) =>
        devices.Where(d => !_settings.Devices.HiddenDevices.Contains(d.Name)).ToList();

    private void ShowBatteries(IReadOnlyList<DeviceBattery> devices)
    {
        var settings = _settings.Devices;
        _flyout?.ViewModel.SetBatteries(Shown(devices), settings.Enabled && settings.ShowInMixer, settings.LowThreshold);
    }

    /// <summary>Reads the batteries again now (the settings page's Refresh).</summary>
    public void RefreshBatteries()
    {
        if (_batteries != null && _settings.Devices.Enabled) _ = _batteries.RefreshAsync();
    }

    /// <summary>While the mixer is open its plaque is on screen: read more often then.</summary>
    public void SetMixerOpen(bool open) => _batteries?.SetFast(open);

    // ---- updates ----------------------------------------------------------------------------------

    public bool CheckUpdatesAutomatically
    {
        get => _settings.Updates.CheckAutomatically;
        set
        {
            _settings.Updates.CheckAutomatically = value;
            SettingsStore.Save(_settings);
        }
    }

    /// <summary>
    /// A minute after start, then daily: announce a new release in the tray (once per release). A thread-pool
    /// timer, so it costs nothing in between; failures (offline) just wait for the next round.
    /// </summary>
    private void StartUpdateChecks(DispatcherQueue dispatcher)
    {
        _updateTimer = new Timer(_ =>
        {
            if (!_settings.Updates.CheckAutomatically) return;
            UpdateInfo? update;
            try
            {
                update = Updater.CheckAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return;
            }
            if (update == null || update.Tag == _settings.Updates.AnnouncedVersion) return;
            dispatcher.TryEnqueue(() =>
            {
                _settings.Updates.AnnouncedVersion = update.Tag;
                SettingsStore.Save(_settings);
                _tray?.Notify("omnideck. update available",
                    $"Version {Updater.Format(update.Version)} is out. Install it in Settings > General.");
            });
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
    }

    /// <summary>The update is unpacked and its script waits for us: quit so it can replace the files.</summary>
    public void ExitForUpdate() => Shutdown();

    public bool Restart(bool runAsAdministrator)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (runAsAdministrator && !isAdministrator)
            {
                if (!AdministratorTask.TryRun()) return false;
            }
            else if (!runAsAdministrator && isAdministrator)
            {
                // The desktop's Explorer process launches with the signed-in user's regular token.
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
                dynamic windows = shell.Windows();
                object location = 0;
                object? root = null;
                dynamic desktop = windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out int _, 1 /* SWFO_NEEDDISPATCH */);
                if (desktop == null) return false;
                desktop.Document.Application.ShellExecute(Environment.ProcessPath,
                    $"{AutoStart.LaunchArgument} {RestartArgument}", AppContext.BaseDirectory, "open", 0);
            }
            else
            {
                using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                    ArgumentList = { AutoStart.LaunchArgument, RestartArgument },
                });
                if (process == null) return false;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("restart", ex);
            return false;
        }

        Shutdown();
        return true;
    }

    private bool TakeInstanceMutex(TimeSpan timeout)
    {
        try { return _singleInstance!.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    public async Task<bool> ConfigureAdministratorTaskAsync(bool enabled)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "open" : "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList = { AdministratorTask.ConfigureArgument, enabled.ToString(), identity.User!.Value },
            };
            using var process = Process.Start(info);
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && AdministratorTask.IsEnabled == enabled;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
        catch (Win32Exception ex)
        {
            CrashLog.Write("configure administrator task", ex);
            return false;
        }
    }

    private void Shutdown()
    {
        _flyout?.StopAnimations();
        _updateTimer?.Dispose();
        _batteries?.Dispose();
        _osd?.Dispose();
        _widget?.Dispose();
        _nowPlaying?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _audio?.Dispose();
        _singleInstance?.ReleaseMutex();
        Exit();
    }
}
