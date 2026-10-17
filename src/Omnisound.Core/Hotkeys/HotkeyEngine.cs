using System.Runtime.InteropServices;

namespace Omnisound.Core.Hotkeys;

/// <summary>
/// Global hotkeys via a low-level keyboard hook on a dedicated thread. Unlike RegisterHotKey it sees
/// key-up too, so one chord can carry press (repeating while held), double and triple-press bindings at once.
/// <see cref="Triggered"/> fires on the thread pool; the hook callback itself only updates state.
/// </summary>
public sealed class HotkeyEngine : IDisposable
{
    public static readonly TimeSpan MultiTapWindow = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan HoldRepeat = TimeSpan.FromMilliseconds(80);

    private readonly object _gate = new();
    private readonly HookProc _proc; // kept alive for the lifetime of the hook
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private IntPtr _hook;

    private Dictionary<KeyChord, ChordState> _chords = [];
    private readonly Dictionary<int, ChordState> _down = []; // vk → chord whose key is currently held
    private Action<KeyChord>? _recorder;
    private int _swallowKeyUp;
    private bool _disposed;

    /// <summary>A binding fired. Raised on a thread-pool thread (repeatedly for held, repeating bindings).</summary>
    public event Action<HotkeyBinding>? Triggered;

    /// <summary>While paused nothing fires and nothing is blocked (e.g. while the settings editor is open).</summary>
    public bool Paused { get; set; }

    public HotkeyEngine()
    {
        _proc = HookCallback;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "Omnisound hotkeys", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait();
    }

    public void SetBindings(IEnumerable<HotkeyBinding> bindings)
    {
        var map = new Dictionary<KeyChord, ChordState>();
        foreach (var group in bindings.Where(b => b.Enabled && !b.Keys.IsEmpty).GroupBy(b => b.Keys))
            map[group.Key] = new ChordState(this, group.ToList());

        lock (_gate)
        {
            foreach (var state in _chords.Values) state.Reset();
            _chords = map;
            _down.Clear();
        }
    }

    /// <summary>
    /// Captures the next key combination instead of firing bindings; the keystroke is swallowed.
    /// Esc on its own cancels (callback gets an empty chord).
    /// </summary>
    public void Record(Action<KeyChord> onRecorded)
    {
        lock (_gate) _recorder = onRecorded;
    }

    public void CancelRecord()
    {
        lock (_gate) _recorder = null;
    }

    // ---- hook ----------------------------------------------------------------------------------

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(null), 0);
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = (int)wParam;
            bool isDown = message is 0x0100 or 0x0104; // WM_KEYDOWN, WM_SYSKEYDOWN
            bool isUp = message is 0x0101 or 0x0105;   // WM_KEYUP, WM_SYSKEYUP
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
            if ((isDown || isUp) && !KeyChord.IsModifierKey(vk) && Handle(vk, isDown))
                return 1; // swallow
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Returns true when the keystroke must be swallowed.</summary>
    private bool Handle(int vk, bool isDown)
    {
        lock (_gate)
        {
            if (_recorder != null && isDown)
            {
                var recorder = _recorder;
                _recorder = null;
                _swallowKeyUp = vk;
                var mods = CurrentModifiers();
                var chord = vk == 0x1B && mods == KeyModifiers.None ? default : new KeyChord(mods, vk);
                ThreadPool.QueueUserWorkItem(_ => recorder(chord));
                return true;
            }
            if (!isDown && vk == _swallowKeyUp)
            {
                _swallowKeyUp = 0;
                return true;
            }

            if (isDown)
            {
                // Auto-repeat of a key we're already tracking: hold logic runs on timers, not on repeats.
                if (_down.TryGetValue(vk, out var held)) return held.Block;
                if (Paused || !_chords.TryGetValue(new KeyChord(CurrentModifiers(), vk), out var state)) return false;
                _down[vk] = state;
                state.OnDown();
                return state.Block;
            }

            // Key-up is matched by key alone: modifiers are often released first.
            if (_down.Remove(vk, out var released))
            {
                released.OnUp();
                return released.Block;
            }
            return false;
        }
    }

    private static KeyModifiers CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var mods = KeyModifiers.None;
        if (Down(0x11)) mods |= KeyModifiers.Ctrl;
        if (Down(0x12)) mods |= KeyModifiers.Alt;
        if (Down(0x10)) mods |= KeyModifiers.Shift;
        if (Down(0x5B) || Down(0x5C)) mods |= KeyModifiers.Win;
        return mods;
    }

    private void Fire(HotkeyBinding binding) =>
        ThreadPool.QueueUserWorkItem(_ => Triggered?.Invoke(binding));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var state in _chords.Values) state.Reset();
        }
        PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- per-chord trigger state machine ---------------------------------------------------------

    /// <summary>
    /// Tap counting for one chord. A press fires on key-down when nothing longer could still follow
    /// (no double/triple on the same keys); otherwise it waits for key-up and the multi-tap window.
    /// A press bound to a stepping action also repeats while held, like keyboard auto-repeat: past
    /// the hold threshold it cancels the tap count and fires every repeat interval until key-up.
    /// All members run under the engine lock.
    /// </summary>
    private sealed class ChordState
    {
        private readonly HotkeyEngine _engine;
        private readonly Dictionary<TriggerKind, HotkeyBinding> _byTrigger = [];
        private readonly int _maxTap;
        private readonly HotkeyBinding? _repeat;
        private int _taps;
        private bool _isDown;
        private bool _holdActive;
        private Timer? _tapTimer;
        private Timer? _holdTimer;

        public bool Block { get; }

        public ChordState(HotkeyEngine engine, List<HotkeyBinding> bindings)
        {
            _engine = engine;
            foreach (var b in bindings) _byTrigger.TryAdd(b.Trigger, b); // first binding per trigger wins
            _maxTap = _byTrigger.Keys.Select(TapCount).DefaultIfEmpty(0).Max();
            _repeat = _byTrigger.TryGetValue(TriggerKind.Single, out var single) && single.Repeats ? single : null;
            Block = bindings.Any(b => b.BlockKey);
        }

        private static int TapCount(TriggerKind trigger) => trigger switch
        {
            TriggerKind.Double => 2,
            TriggerKind.Triple => 3,
            _ => 1,
        };

        public void OnDown()
        {
            _isDown = true;
            _taps++;
            DisposeTimer(ref _tapTimer);

            bool firstPress = _taps == 1;
            if (_taps >= _maxTap) FireTaps(); // nothing longer is bound: no need to wait
            if (_repeat != null && firstPress)
                _holdTimer = new Timer(_ => OnHoldTick(), null, HoldThreshold, HoldRepeat);
        }

        public void OnUp()
        {
            _isDown = false;
            DisposeTimer(ref _holdTimer);
            if (_holdActive)
            {
                _holdActive = false;
                _taps = 0;
                return;
            }
            if (_taps == 0) return; // already fired on key-down

            if (_taps >= _maxTap) FireTaps();
            else _tapTimer = new Timer(_ => OnTapWindowElapsed(), null, MultiTapWindow, Timeout.InfiniteTimeSpan);
        }

        private void OnTapWindowElapsed()
        {
            lock (_engine._gate)
            {
                if (!_isDown && _taps > 0) FireTaps();
            }
        }

        private void OnHoldTick()
        {
            lock (_engine._gate)
            {
                if (!_isDown) return;
                _holdActive = true;
                _taps = 0;
                DisposeTimer(ref _tapTimer);
                _engine.Fire(_repeat!);
            }
        }

        private void FireTaps()
        {
            var trigger = _taps switch { 1 => TriggerKind.Single, 2 => TriggerKind.Double, _ => TriggerKind.Triple };
            _taps = 0;
            if (_byTrigger.TryGetValue(trigger, out var binding)) _engine.Fire(binding);
        }

        public void Reset()
        {
            DisposeTimer(ref _tapTimer);
            DisposeTimer(ref _holdTimer);
            _taps = 0;
            _isDown = false;
            _holdActive = false;
        }

        private static void DisposeTimer(ref Timer? timer)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    // ---- Win32 ---------------------------------------------------------------------------------

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
