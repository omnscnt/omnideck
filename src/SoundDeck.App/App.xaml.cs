using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SoundDeck.Core;
using SoundDeck.Services;
using SoundDeck.ViewModels;
using SoundDeck.Views;

namespace SoundDeck;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AudioService? _audio;
    private TrayIconHost? _tray;
    private FlyoutWindow? _flyout;

    /// <summary>The sound core, shared by the mixer and (later) hotkeys.</summary>
    public static AudioService Audio => ((App)Current)._audio!;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, @"Local\SoundDeck.SingleInstance", out bool isFirst);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SoundDeck.ShowFlyout");
        var menuSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SoundDeck.ShowMenu");
        var cmdArgs = Environment.GetCommandLineArgs();
        if (!isFirst)
        {
            // Launching the exe again pops the running instance's mixer (or, with --menu, its tray menu).
            if (!cmdArgs.Contains(AutoStart.LaunchArgument))
            {
                AllowSetForegroundWindow(-1); // Hand our foreground right to the running instance.
                (cmdArgs.Contains("--menu") ? menuSignal : showSignal).Set();
            }
            Exit();
            return;
        }

        // Tray app: hiding the flyout must not end the process.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Core Audio session notifications want an MTA thread; the objects themselves are free-threaded.
        _audio = await Task.Run(() => new AudioService());

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _flyout = new FlyoutWindow(new MixerViewModel(_audio, dispatcher));
        _tray = new TrayIconHost(() => _flyout.Toggle(), Shutdown);

        new Thread(() =>
        {
            var signals = new WaitHandle[] { showSignal, menuSignal };
            while (true)
            {
                if (WaitHandle.WaitAny(signals) == 0) dispatcher.TryEnqueue(() => _flyout.Show());
                else dispatcher.TryEnqueue(() => _tray.ShowMenu());
            }
        }) { IsBackground = true, Name = "SoundDeck instance signals" }.Start();

        bool fromAutostart = cmdArgs.Contains(AutoStart.LaunchArgument);
        if (!fromAutostart) _flyout.Show(); // Manual launch: show that we're alive.
        else _flyout.Prewarm(); // tray-only start: warm the window up so the first open is clean
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private void Shutdown()
    {
        _tray?.Dispose();
        _audio?.Dispose();
        _singleInstance?.ReleaseMutex();
        Exit();
    }
}
