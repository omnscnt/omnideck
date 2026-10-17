using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Omnisound.Core;
using Omnisound.Core.Hotkeys;
using Omnisound.Core.Media;
using Omnisound.Services;
using Omnisound.ViewModels;
using Omnisound.Views;

namespace Omnisound;

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

    public static App Instance => (App)Current;

    /// <summary>The sound core, shared by the mixer and hotkeys.</summary>
    public static AudioService Audio => Instance._audio!;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, @"Local\Omnisound.SingleInstance", out bool isFirst);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnisound.ShowFlyout");
        var menuSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnisound.ShowMenu");
        var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Omnisound.ShowSettings");
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
        AutoStart.Migrate();
        _runner = new HotkeyActionRunner(_audio, new MediaSessionService());
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
        }) { IsBackground = true, Name = "Omnisound instance signals" }.Start();

        bool fromAutostart = cmdArgs.Contains(AutoStart.LaunchArgument);
        if (!fromAutostart) _flyout.Show(); // Manual launch: show that we're alive.
        else _flyout.Prewarm(); // tray-only start: warm the window up so the first open is clean
    }

    /// <summary>Opens (or brings forward) the settings window.</summary>
    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            var hotkeys = new HotkeysViewModel(_settings.Hotkeys, ApplyHotkeys, _hotkeys!, CurrentAppNames);
            _settingsWindow = new SettingsWindow(hotkeys);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
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

    private void Shutdown()
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _audio?.Dispose();
        _singleInstance?.ReleaseMutex();
        Exit();
    }
}
