using System.Runtime.InteropServices;

namespace Omnideck.Core.Hotkeys;

/// <summary>
/// Global hotkeys via a low-level keyboard hook on a dedicated thread. Unlike RegisterHotKey it sees
/// key-up too, so one chord can carry press, double, triple-press and press-and-hold bindings at once.
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

    private (KeyChord Keys, ChordState State)[] _chords = [];
    private readonly HashSet<int> _pressed = [];
    private readonly HashSet<int> _swallowed = [];
    private readonly List<(KeyChord Keys, ChordState State)> _active = [];
    private readonly HashSet<int> _recordedKeys = [];
    private Action<KeyChord>? _recorder;
    private bool _disposed;

    /// <summary>A binding fired. Raised on a thread-pool thread (repeatedly for held, repeating bindings).</summary>
    public event Action<HotkeyBinding>? Triggered;

    /// <summary>Caps, Num or Scroll Lock was pressed (virtual-key code). Raised on the thread pool, once per press.</summary>
    public event Action<int>? LockKeyPressed;

    /// <summary>
    /// A key that ends a keyboard-layout switch was released (Shift, Ctrl, Alt, Win, Space): the input
    /// language may have just changed. Raised on the thread pool.
    /// </summary>
    public event Action? LayoutKeyReleased;

    private int _lockKeyDown; // the lock key being held, so auto-repeat doesn't count as more presses

    /// <summary>While paused nothing fires and nothing is blocked (e.g. while the settings editor is open).</summary>
    public bool Paused { get; set; }

    public HotkeyEngine()
    {
        _proc = HookCallback;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "Omnideck hotkeys", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait();
    }

    public void SetBindings(IEnumerable<HotkeyBinding> bindings)
    {
        var chords = bindings.Where(b => b.Enabled && !b.Keys.IsEmpty)
            .GroupBy(b => b.Keys)
            .OrderByDescending(group => group.Key.KeyCount)
            .ToArray();
        var states = chords
            .Select(group => (group.Key, new ChordState(this, group.ToList(),
                chords.Any(other => other.Key.Extends(group.Key)))))
            .ToArray();

        lock (_gate)
        {
            foreach (var (_, state) in _chords) state.Reset();
            _chords = states;
            _active.Clear();
        }
    }

    /// <summary>
    /// Captures keys held together until the first participating key is released.
    /// Esc on its own cancels (callback gets an empty chord).
    /// </summary>
    public void Record(Action<KeyChord> onRecorded)
    {
        lock (_gate)
        {
            _recordedKeys.Clear();
            _recorder = onRecorded;
        }
    }

    public void CancelRecord()
    {
        lock (_gate)
        {
            _recordedKeys.Clear();
            _recorder = null;
        }
    }

    // ---- hook ----------------------------------------------------------------------------------

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        // Seed modifiers already held when the hook starts. After that, track hook events directly.
        foreach (int vk in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) _pressed.Add(vk);
        }
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
            if (isDown || isUp) WatchSystemKeys(vk, isDown);
            if ((isDown || isUp) && Handle(vk, isDown))
                return 1; // swallow
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Lock keys and layout-switch keys, for the on-screen notices. Cheap: the hook must stay fast.</summary>
    private void WatchSystemKeys(int vk, bool isDown)
    {
        if (vk is 0x14 or 0x90 or 0x91) // VK_CAPITAL, VK_NUMLOCK, VK_SCROLL
        {
            if (isDown && _lockKeyDown != vk) ThreadPool.QueueUserWorkItem(_ => LockKeyPressed?.Invoke(vk));
            _lockKeyDown = isDown ? vk : 0;
        }
        else if (!isDown && vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C or 0x20)
        {
            ThreadPool.QueueUserWorkItem(_ => LayoutKeyReleased?.Invoke());
        }
    }

    /// <summary>Returns true when the keystroke must be swallowed.</summary>
    private bool Handle(int vk, bool isDown)
    {
        lock (_gate)
        {
            if (!isDown)
            {
                if (_recorder != null && _recordedKeys.Count > 0 &&
                    (_recordedKeys.Contains(vk) || KeyChord.ModifierFor(vk) != KeyModifiers.None))
                {
                    var keys = _recordedKeys.Where(key => KeyChord.ModifierFor(key) == KeyModifiers.None).ToArray();
                    FinishRecording(new KeyChord(CurrentModifiers(), 0, keys));
                }

                _pressed.Remove(vk);
                var mods = CurrentModifiers();
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    var (keys, state) = _active[i];
                    if ((mods & keys.Modifiers) == keys.Modifiers && keys.KeysAreDown(_pressed)) continue;
                    _active.RemoveAt(i);
                    state.OnUp();
                }
                return _swallowed.Remove(vk);
            }

            // Every down is tracked, including unbound keys. Auto-repeat never changes a match.
            if (!_pressed.Add(vk)) return _swallowed.Contains(vk);
            var modifier = KeyChord.ModifierFor(vk);
            var currentModifiers = CurrentModifiers();
            if (_recorder != null)
            {
                _swallowed.Add(vk);
                if (vk == 0x1B && currentModifiers == KeyModifiers.None && _recordedKeys.Count == 0)
                    FinishRecording(default);
                else
                    _recordedKeys.Add(vk);
                return true;
            }

            if (Paused) return false;
            foreach (var (keys, state) in _chords)
            {
                if (keys.Modifiers != currentModifiers || !keys.KeysAreDown(_pressed)) continue;
                if (modifier == KeyModifiers.None ? !keys.ContainsKey(vk) : !keys.Modifiers.HasFlag(modifier)) continue;
                if (keys.KeyCount == 0 && _pressed.Any(key => KeyChord.ModifierFor(key) == KeyModifiers.None)) continue;
                if (state.IsDown) return false;

                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    var (shorter, pending) = _active[i];
                    if (!keys.Extends(shorter)) continue;
                    _active.RemoveAt(i);
                    pending.Reset();
                }
                _active.Add((keys, state));
                state.OnDown();
                if (state.Block) _swallowed.Add(vk);
                return state.Block;
            }

            return false;
        }
    }

    private void FinishRecording(KeyChord chord)
    {
        var recorder = _recorder!;
        _recorder = null;
        _recordedKeys.Clear();
        ThreadPool.QueueUserWorkItem(_ => recorder(chord));
    }

    private KeyModifiers CurrentModifiers()
    {
        var mods = KeyModifiers.None;
        foreach (int vk in _pressed) mods |= KeyChord.ModifierFor(vk);
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
            foreach (var (_, state) in _chords) state.Reset();
        }
        PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- per-chord trigger state machine ---------------------------------------------------------

    /// <summary>
    /// Tap counting for one chord. A press fires on key-down when nothing else could still follow
    /// (no double/triple or hold on the same keys); otherwise it waits for key-up and the multi-tap
    /// window. Holding past the threshold cancels the tap count and fires the hold: a Hold binding
    /// if there is one, else a press bound to a stepping action (like keyboard auto-repeat). Holds
    /// on stepping actions repeat every interval until key-up. All members run under the engine lock.
    /// </summary>
    private sealed class ChordState
    {
        private readonly HotkeyEngine _engine;
        private readonly Dictionary<TriggerKind, HotkeyBinding> _byTrigger = [];
        private readonly int _maxTap;
        private readonly HotkeyBinding? _holdBinding; // what a long press fires, if anything
        private readonly bool _waitForHold;           // a Hold binding exists: a press can't fire until key-up
        private readonly bool _canExtend;
        private int _taps;
        private bool _isDown;
        private bool _holdActive;
        private Timer? _tapTimer;
        private Timer? _holdTimer;

        public bool Block { get; }
        public bool IsDown => _isDown;

        public ChordState(HotkeyEngine engine, List<HotkeyBinding> bindings, bool canExtend)
        {
            _engine = engine;
            _canExtend = canExtend;
            foreach (var b in bindings) _byTrigger.TryAdd(b.Trigger, b); // first binding per trigger wins
            _maxTap = _byTrigger.Keys.Where(t => t != TriggerKind.Hold).Select(TapCount).DefaultIfEmpty(0).Max();
            _waitForHold = _byTrigger.TryGetValue(TriggerKind.Hold, out var hold);
            _holdBinding = hold ?? (_byTrigger.TryGetValue(TriggerKind.Single, out var single) && single.Repeats ? single : null);
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
            if (!_canExtend && !_waitForHold && _taps >= _maxTap) FireTaps();
            if (_holdBinding != null && firstPress)
            {
                var repeat = _holdBinding.Repeats ? HoldRepeat : Timeout.InfiniteTimeSpan;
                _holdTimer = new Timer(_ => OnHoldTick(), null, HoldThreshold, repeat);
            }
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
                _engine.Fire(_holdBinding!);
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
        public uint lPrivate;
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
