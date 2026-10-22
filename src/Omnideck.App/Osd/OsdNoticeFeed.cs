using System.Globalization;
using System.Runtime.InteropServices;
using Omnideck.Core;
using Omnideck.Core.Hotkeys;

namespace Omnideck.Osd;

/// <summary>
/// The text notices next to the volume bar: microphone muted or on, a lock key turned on or off, the
/// keyboard layout switched, the default output device changed. Each shows only when its kind is on.
/// Everything here reacts to events; nothing polls.
/// </summary>
public sealed class OsdNoticeFeed
{
    // Segoe Fluent Icons.
    private const string MicGlyph = "", MicOffGlyph = "", SpeakerGlyph = "";

    private readonly AudioService _audio;
    private readonly VolumeOsd _osd;
    private readonly Func<VolumeOsdSettings> _settings;
    private readonly object _gate = new();
    private MasterVolume? _watchedMic;
    private bool _micMuted;
    private string? _lastDeviceId;
    private readonly Timer _layoutCheck;
    private IntPtr _layoutWindow; // the foreground window the last layout was read from
    private IntPtr _layout;

    public OsdNoticeFeed(AudioService audio, HotkeyEngine hotkeys, VolumeOsd osd, Func<VolumeOsdSettings> settings)
    {
        _audio = audio;
        _osd = osd;
        _settings = settings;

        WatchMicrophone();
        audio.MicrophoneChanged += (_, _) => WatchMicrophone();

        _lastDeviceId = audio.Master?.DeviceId;
        audio.DeviceChanged += (_, _) => OnDeviceChanged();

        hotkeys.LockKeyPressed += OnLockKeyPressed;

        // Layout switches finish on a key release; the window applies the new layout a moment later.
        _layoutCheck = new Timer(_ => CheckLayout(), null, Timeout.Infinite, Timeout.Infinite);
        hotkeys.LayoutKeyReleased += () => _layoutCheck.Change(150, Timeout.Infinite);
        ReadLayout(out _layoutWindow, out _layout);
    }

    // ---- microphone ------------------------------------------------------------------------------

    private void WatchMicrophone()
    {
        var mic = _audio.Microphone;
        lock (_gate)
        {
            if (ReferenceEquals(mic, _watchedMic)) return;
            _watchedMic = mic;
            _micMuted = mic?.IsMuted ?? false;
        }
        if (mic != null) mic.VolumeChanged += (_, e) => OnMicrophoneChanged(mic, e.IsMuted);
    }

    /// <summary>Only the mute flag matters here (by a hotkey, a headset button, another app).</summary>
    private void OnMicrophoneChanged(MasterVolume mic, bool muted)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(mic, _watchedMic) || muted == _micMuted) return;
            _micMuted = muted;
        }
        if (_settings().ShowMicrophone)
            _osd.Show(OsdContent.Message("mic", muted ? MicOffGlyph : MicGlyph, muted ? "Microphone off" : "Microphone on", dimmed: muted));
    }

    // ---- output device ---------------------------------------------------------------------------

    private void OnDeviceChanged()
    {
        var master = _audio.Master;
        if (master == null || master.DeviceId == _lastDeviceId) return;
        _lastDeviceId = master.DeviceId;
        if (_settings().ShowOutputDevice)
            _osd.Show(OsdContent.Message("device", SpeakerGlyph, master.DeviceName));
    }

    // ---- lock keys -------------------------------------------------------------------------------

    private void OnLockKeyPressed(int vk)
    {
        if (!_settings().ShowLockKeys) return;
        // The toggle flips as the key goes down; read it once Windows has applied it.
        Thread.Sleep(60);
        bool on = (GetKeyState(vk) & 1) != 0;
        var (name, label) = vk switch { 0x14 => ("Caps Lock", "A"), 0x90 => ("Num Lock", "1"), _ => ("Scroll Lock", "2195") };
        _osd.Show(OsdContent.KeyNotice("lock:" + vk, label, on, $"{name} {(on ? "on" : "off")}", dimmed: !on));
    }

    // ---- keyboard layout -------------------------------------------------------------------------

    /// <summary>
    /// The layout is per window: switching windows changes it without the user switching anything, so
    /// only a change within the same foreground window counts.
    /// </summary>
    private void CheckLayout()
    {
        ReadLayout(out var window, out var layout);
        bool switched;
        lock (_gate)
        {
            switched = window == _layoutWindow && layout != _layout && layout != IntPtr.Zero;
            _layoutWindow = window;
            _layout = layout;
        }
        if (!switched || !_settings().ShowKeyboardLayout) return;
        var (code, name) = Language(layout);
        _osd.Show(OsdContent.KeyNotice("layout", code, null, name));
    }

    private static void ReadLayout(out IntPtr window, out IntPtr layout)
    {
        window = GetForegroundWindow();
        uint thread = window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, out _);
        layout = thread == 0 ? IntPtr.Zero : GetKeyboardLayout(thread);
    }

    /// <summary>
    /// The key cap's code ("EN", "RU") and the language as its speakers write it ("English (United States)",
    /// "Русский (Россия)").
    /// </summary>
    private static (string Code, string Name) Language(IntPtr layout)
    {
        int langId = (int)((long)layout & 0xFFFF);
        try
        {
            var culture = new CultureInfo(langId);
            string name = culture.NativeName;
            name = name.Length > 0 ? char.ToUpper(name[0], culture) + name[1..] : culture.Name;
            return (culture.TwoLetterISOLanguageName.ToUpperInvariant(), name);
        }
        catch (CultureNotFoundException)
        {
            return ("?", $"Layout {langId:X4}");
        }
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
}
