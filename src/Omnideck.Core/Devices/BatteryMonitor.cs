namespace Omnideck.Core.Devices;

/// <summary>
/// Reads every source now and then (a few minutes apart: batteries change slowly, and asking a mouse
/// over its dongle is device I/O) and on demand. Readings and <see cref="Changed"/> come from a
/// thread-pool thread; refreshes never overlap.
/// </summary>
public sealed class BatteryMonitor : IDisposable
{
    private readonly IBatterySource[] _sources = [new SteelSeriesBattery(), new BluetoothBattery()];
    private readonly Timer _timer;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private TimeSpan _interval = TimeSpan.FromMinutes(5);
    private volatile IReadOnlyList<DeviceBattery> _devices = [];
    private DateTime _lastRead = DateTime.MinValue;

    public event Action<IReadOnlyList<DeviceBattery>>? Changed;

    public IReadOnlyList<DeviceBattery> Devices => _devices;

    public BatteryMonitor() => _timer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Starts (or restarts) the polling; a first read follows shortly. Stopped with <see cref="Stop"/>.</summary>
    public void Start(TimeSpan interval)
    {
        _interval = interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : interval;
        _timer.Change(TimeSpan.FromSeconds(3), _interval);
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _devices = [];
        Changed?.Invoke(_devices);
    }

    /// <summary>Reads again unless the last read is younger than <paramref name="maxAge"/> (the mixer opening).</summary>
    public void RefreshIfOlderThan(TimeSpan maxAge)
    {
        if (DateTime.UtcNow - _lastRead > maxAge) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return; // one is running: it brings fresh readings
        try
        {
            var devices = await Task.Run(() =>
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
            }).ConfigureAwait(false);
            _lastRead = DateTime.UtcNow;
            _devices = devices;
            Changed?.Invoke(devices);
        }
        finally
        {
            _busy.Release();
        }
    }

    public void Dispose() => _timer.Dispose();
}
