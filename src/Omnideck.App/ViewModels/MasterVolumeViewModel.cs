using Microsoft.UI.Dispatching;
using Omnideck.Core;

namespace Omnideck.ViewModels;

public sealed partial class MasterVolumeViewModel : VolumeViewModelBase, IDisposable
{
    private readonly MasterVolume _master;

    public string DeviceName => _master.DeviceName;

    public MasterVolumeViewModel(MasterVolume master, DispatcherQueue dispatcher)
        : base(dispatcher, master.Volume, master.IsMuted)
    {
        _master = master;
        master.VolumeChanged += OnCoreVolumeChanged;
    }

    protected override float ReadPeak() => _master.Peak;

    protected override void ApplyVolume(float volume) => _master.SetVolume(volume, UiContext);

    protected override void ApplyMute(bool mute) => _master.SetMute(mute, UiContext);

    public void Dispose() => _master.VolumeChanged -= OnCoreVolumeChanged;
}
