using CommunityToolkit.Mvvm.ComponentModel;

namespace Omnideck.ViewModels;

/// <summary>A device on the Devices page: its battery and whether it shows on the plaque (and gets a low battery notice).</summary>
public sealed partial class DeviceRow(BatteryChip chip, bool shown, Action<DeviceRow> changed) : ObservableObject
{
    public BatteryChip Chip { get; } = chip;

    [ObservableProperty]
    private bool _isShown = shown;

    partial void OnIsShownChanged(bool value) => changed(this);
}
