using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Omnideck.Core;
using Omnideck.Core.Hotkeys;
using Omnideck.Core.Media;
using Omnideck.Osd;
using Omnideck.Services;
using Omnideck.Taskbar;
using Omnideck.ViewModels;
using Omnideck.Views;

namespace Omnideck;

public partial class App : Application
{
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
    private Timer? _updateTimer;

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
        _singleInstance = new Mutex(true, @"Local\Omnideck.SingleInstance", out bool isFirst);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnideck.ShowFlyout");
        var menuSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnideck.ShowMenu");
        var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnideck.ShowSettings");
        var cmdArgs = Environment.GetCommandLineArgs();
        if (!isFirst)
        {
            // Launching the exe again pops the running instance's mixer (--menu: tray menu, --settings: settings).
            if (!cmdArgs.Contains(AutoStart.LaunchArgument))
            {
                AllowSetForegroundWindow(-1); // Hand our foreground right to the running instance.
                (cmdArgs.Contains("--menu") ? menuSignal : cmdArgs.Contains("--settings") ? settingsSignal : showSignal).Set();
            }
            Exit();
            return;
        }

        // Tray app: hiding the flyout must not end the process.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Core Audio session notifications want an MTA thread; the objects themselves are free-threaded.
        _audio = await Task.Run(() => new AudioService());

        _settings = SettingsStore.Load();
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

        _flyout = new FlyoutWindow(new MixerViewModel(_audio, dispatcher));
        _tray = new TrayIconHost(() => _flyout.Toggle(), ShowSettings, Shutdown);

        _nowPlaying = new NowPlayingWatcher(media);
        _nowPlaying.Filter = _settings.TaskbarWidget.ToFilter();
        _widget = new TaskbarWidget(_nowPlaying);
        _widget.Configure(_settings.TaskbarWidget);
        _ = _nowPlaying.StartAsync();

        _osd = new VolumeOsd();
        _osd.Configure(_settings.VolumeOsd);
        _osdFeed = new VolumeOsdFeed(_audio, _runner, _osd, () => _settings.VolumeOsd.ShowExternalChanges);

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
        bool fromAutostart = cmdArgs.Contains(AutoStart.LaunchArgument) || updated;
        if (updated) _tray.Notify("omnideck. updated", $"Version {Updater.CurrentText} is installed.");
        if (updated) _ = Task.Run(InstallLayout.RemoveFlatLeftovers); // first start after leaving the flat layout
        StartUpdateChecks(dispatcher);
        if (!fromAutostart) _flyout.Show(); // Manual launch: show that we're alive.
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

    private void Shutdown()
    {
        _updateTimer?.Dispose();
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
