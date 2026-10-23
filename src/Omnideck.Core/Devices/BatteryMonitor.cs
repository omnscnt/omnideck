using System.Runtime.InteropServices;

namespace Omnideck.Core.Devices;

/// <summary>
/// Reads every source now and then (minutes apart: batteries change slowly, and asking a mouse over its
/// dongle is device I/O), more often while the mixer shows them, and right after a device is plugged in
/// or out (a mouse put on its charging cable). Readings and <see cref="Changed"/> come from a thread-pool
/// thread; refreshes never overlap, and one asked for during another runs after it.
/// </summary>
public sealed class BatteryMonitor : IDisposable
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PlugSettle = TimeSpan.FromSeconds(1.5); // a new device needs a moment before it answers

    private readonly IBatterySource[] _sources = [new SteelSeriesBattery(), new BluetoothBattery()];
    private readonly Timer _timer, _plugTimer;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CmNotifyCallback _plugCallback; // kept alive while registered
    private TimeSpan _interval = TimeSpan.FromMinutes(5);
    private bool _running, _fast;
    private int _again;
    private IntPtr _plugNotification;
    private volatile IReadOnlyList<DeviceBattery> _devices = [];
    private DateTime _lastRead = DateTime.MinValue;

    public event Action<IReadOnlyList<DeviceBattery>>? Changed;

    public IReadOnlyList<DeviceBattery> Devices => _devices;

    public BatteryMonitor()
    {
        _timer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _plugTimer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _plugCallback = OnPlug;
    }

    /// <summary>Starts (or restarts) the polling; a first read follows shortly. Stopped with <see cref="Stop"/>.</summary>
    public void Start(TimeSpan interval)
    {
        _interval = interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : interval;
        _running = true;
        _timer.Change(TimeSpan.FromSeconds(3), Period);
        WatchPlugging();
    }

    public void Stop()
    {
        _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        StopWatchingPlugging();
        _devices = [];
        Changed?.Invoke(_devices);
    }

    /// <summary>Faster polling while the readings are on screen (the mixer open), back to the set interval after.</summary>
    public void SetFast(bool fast)
    {
        if (fast == _fast) return;
        _fast = fast;
        if (!_running) return;
        if (fast && DateTime.UtcNow - _lastRead > TimeSpan.FromSeconds(10)) _ = RefreshAsync(); // stale: read now
        var sinceLast = DateTime.UtcNow - _lastRead;
        var due = Period - sinceLast;
        _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Period);
    }

    private TimeSpan Period => _fast ? FastInterval : _interval;

    public async Task RefreshAsync()
    {
        if (!await _busy.WaitAsync(0).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _again, 1); // a read is running: one more after it, for whatever changed meanwhile
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _again, 0);
                var devices = await Task.Run(ReadAll).ConfigureAwait(false);
                _lastRead = DateTime.UtcNow;
                _devices = devices;
                Changed?.Invoke(devices);
            }
            while (Interlocked.Exchange(ref _again, 0) == 1);
        }
        finally
        {
            _busy.Release();
        }
    }

    private List<DeviceBattery> ReadAll()
    {
        var all = new List<DeviceBattery>();
        foreach (var source in _sources)
        {
            try { all.AddRange(source.Read()); }
            catch (Exception) { /* a source failing must not hide the others */ }
        }
#if DEBUG
        // Layout tests without a device at hand: OMNIDECK_FAKE_BATTERIES=1.
        if (Environment.GetEnvironmentVariable("OMNIDECK_FAKE_BATTERIES") == "1")
        {
            all.Add(new DeviceBattery("fake:mouse", "Test Mouse", DeviceKind.Mouse, 8, false, "Test"));
            all.Add(new DeviceBattery("fake:buds", "Test Buds", DeviceKind.Headset, 70, true, "Test"));
        }
#endif
        return all;
    }

    // ---- plugging: a HID device coming or going ------------------------------------------------------

    private void WatchPlugging()
    {
        if (_plugNotification != IntPtr.Zero) return;
        var filter = new CM_NOTIFY_FILTER
        {
            cbSize = Marshal.SizeOf<CM_NOTIFY_FILTER>(),
            FilterType = 0, // CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE
            ClassGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030"), // HID
        };
        if (CM_Register_Notification(ref filter, IntPtr.Zero, _plugCallback, out var handle) == 0) _plugNotification = handle;
    }

    private void StopWatchingPlugging()
    {
        if (_plugNotification == IntPtr.Zero) return;
        CM_Unregister_Notification(_plugNotification);
        _plugNotification = IntPtr.Zero;
    }

    /// <summary>A HID interface arrived or left (a cable plugged in sends a burst of them): read once things settle.</summary>
    private int OnPlug(IntPtr notification, IntPtr context, int action, IntPtr data, int size)
    {
        if (action is 0 or 1 /* INTERFACE_ARRIVAL, INTERFACE_REMOVAL */) _plugTimer.Change(PlugSettle, Timeout.InfiniteTimeSpan);
        return 0;
    }

    private delegate int CmNotifyCallback(IntPtr notification, IntPtr context, int action, IntPtr data, int size);

    // The union after the header is as large as its biggest member (a 200-character instance ID).
    [StructLayout(LayoutKind.Explicit, Size = 416, CharSet = CharSet.Unicode)]
    private struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)] public int cbSize;
        [FieldOffset(4)] public int Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(12)] public int Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [DllImport("cfgmgr32.dll")] private static extern int CM_Register_Notification(ref CM_NOTIFY_FILTER filter, IntPtr context, CmNotifyCallback callback, out IntPtr notification);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Unregister_Notification(IntPtr notification);

    public void Dispose()
    {
        StopWatchingPlugging();
        _timer.Dispose();
        _plugTimer.Dispose();
    }
}
