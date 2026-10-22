using System.Runtime.InteropServices;
using Omnideck.Core.Media;

namespace Omnideck.Core.Hotkeys;

/// <summary>Turns a fired binding into a volume change or a media command on the right target.</summary>
public sealed class HotkeyActionRunner(AudioService audio, MediaSessionService media)
{
    private readonly SemaphoreSlim _serial = new(1, 1);

    /// <summary>Raised after a volume action, with the control that changed (for an on-screen indicator).</summary>
    public event Action<HotkeyBinding, IVolumeControl>? VolumeChanged;

    public async Task RunAsync(HotkeyBinding binding)
    {
        if (binding.IsAppAction) return; // e.g. Open mixer: handled by the app
        // Serialize: held keys repeat every 80 ms and steps must not interleave.
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (binding.IsVolumeAction) await RunVolumeAsync(binding).ConfigureAwait(false);
            else await RunMediaAsync(binding).ConfigureAwait(false);
        }
        catch (COMException)
        {
            // Target session vanished mid-action; nothing to do.
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task RunVolumeAsync(HotkeyBinding binding)
    {
        var target = await ResolveVolumeTargetAsync(binding).ConfigureAwait(false);
        if (target == null) return;

        float step = Math.Clamp(binding.Step, 1, 100) / 100f;
        switch (binding.Action)
        {
            case HotkeyAction.VolumeUp:
                if (target.IsMuted) target.ToggleMute(); // raising the volume of a muted target unmutes it, like the media keys
                target.AdjustVolume(step);
                break;
            case HotkeyAction.VolumeDown:
                target.AdjustVolume(-step);
                break;
            case HotkeyAction.ToggleMute:
                target.ToggleMute();
                break;
        }
        VolumeChanged?.Invoke(binding, target);
    }

    private async Task<IVolumeControl?> ResolveVolumeTargetAsync(HotkeyBinding binding) => binding.Target switch
    {
        HotkeyTarget.Master => audio.Master,
        HotkeyTarget.ActiveWindow => ForegroundProcessId() is int pid ? audio.FindAppByProcessId(pid) : null,
        HotkeyTarget.MediaPlayer => await media.GetCurrentAppNameAsync().ConfigureAwait(false) is { } name ? audio.FindApp(name) : null,
        HotkeyTarget.App => string.IsNullOrWhiteSpace(binding.AppName) ? null : audio.FindApp(binding.AppName),
        _ => null,
    };

    private async Task RunMediaAsync(HotkeyBinding binding)
    {
        // Media commands go to a session: Windows' current one, or the session of the targeted app.
        string? app = binding.Target switch
        {
            HotkeyTarget.App => binding.AppName,
            HotkeyTarget.ActiveWindow => ForegroundProcessId() is int pid ? ProcessNameOf(pid) : null,
            _ => null,
        };

        switch (binding.Action)
        {
            case HotkeyAction.PlayPause: await media.TogglePlayPauseAsync(app).ConfigureAwait(false); break;
            case HotkeyAction.NextTrack: await media.NextAsync(app).ConfigureAwait(false); break;
            case HotkeyAction.PreviousTrack: await media.PreviousAsync(app).ConfigureAwait(false); break;
        }
    }

    private static int? ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 ? null : (int)pid;
    }

    private static string? ProcessNameOf(int pid) =>
        ProcessInfo.GetExecutablePath(pid) is { } path ? Path.GetFileNameWithoutExtension(path) : null;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
