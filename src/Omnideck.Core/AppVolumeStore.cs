using System.Diagnostics;
using System.Text.Json;

namespace Omnideck.Core;

internal sealed class AppVolumeStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, float> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public AppVolumeStore(string path)
    {
        _path = path;
        try
        {
            using var file = File.OpenRead(path);
            var saved = JsonSerializer.Deserialize<Dictionary<string, float>>(file);
            if (saved == null) return;
            foreach (var (key, volume) in saved)
            {
                if (volume is >= 0f and <= 1f) _volumes[key] = volume;
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not load app volumes: {0}", ex.Message);
        }
    }

    public float? Get(string key)
    {
        lock (_gate) return _volumes.TryGetValue(key, out var volume) ? volume : null;
    }

    public void Save(string key, float volume)
    {
        lock (_gate)
        {
            if (!_volumes.TryGetValue(key, out var previous) || previous != volume)
            {
                _volumes[key] = volume;
                _dirty = true;
            }
            if (!_dirty) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_volumes));
                File.Move(temp, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not save app volumes: {0}", ex.Message);
            }
        }
    }
}
