using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Omnisound.Core.Hotkeys;

namespace Omnisound.ViewModels;

/// <summary>One row in the hotkey list.</summary>
public sealed partial class HotkeyItemViewModel : ObservableObject
{
    public HotkeysViewModel Owner { get; }

    public HotkeyBinding Binding { get; private set; }

    /// <summary>Added in this window and never saved with keys yet.</summary>
    public bool IsNew { get; set; }

    /// <summary>The card's editor is open; kept here so it survives container recycling and reordering.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>Open (animated) and start recording as soon as the card appears.</summary>
    public bool PendingExpand { get; set; }

    public HotkeyItemViewModel(HotkeyBinding binding, HotkeysViewModel owner)
    {
        Binding = binding;
        Owner = owner;
    }

    public IReadOnlyList<string> KeyParts => Binding.Keys.Parts;

    public Visibility PlaceholderVisibility => Binding.Keys.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public string Summary => Describe(Binding);

    public bool Enabled
    {
        get => Binding.Enabled;
        set
        {
            if (Binding.Enabled == value) return;
            Binding.Enabled = value;
            OnPropertyChanged();
            Owner.Save();
        }
    }

    public void Replace(HotkeyBinding binding)
    {
        Binding = binding;
        OnPropertyChanged(string.Empty); // everything changed
    }

    public static string Describe(HotkeyBinding b)
    {
        string trigger = b.Trigger switch
        {
            TriggerKind.Double => "Double press",
            TriggerKind.Triple => "Triple press",
            _ => b.Repeats ? "Press or hold" : "Press",
        };
        string action = b.Action switch
        {
            HotkeyAction.VolumeUp => $"Volume up {b.Step}%",
            HotkeyAction.VolumeDown => $"Volume down {b.Step}%",
            HotkeyAction.ToggleMute => "Mute / unmute",
            HotkeyAction.PlayPause => "Play / pause",
            HotkeyAction.NextTrack => "Next track",
            HotkeyAction.PreviousTrack => "Previous track",
            _ => "Open mixer",
        };
        return b.IsAppAction ? $"{trigger} · {action}" : $"{trigger} · {action} · {TargetName(b)}";
    }

    public static string TargetName(HotkeyBinding b) => b.Target switch
    {
        HotkeyTarget.ActiveWindow => "Active window",
        HotkeyTarget.MediaPlayer => "Media player",
        HotkeyTarget.App => string.IsNullOrWhiteSpace(b.AppName) ? "App" : b.AppName!,
        _ => b.IsVolumeAction ? "Master volume" : "Current media",
    };
}
