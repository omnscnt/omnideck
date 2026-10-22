using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Omnideck.Core.Hotkeys;
using Omnideck.Services;

namespace Omnideck.ViewModels;

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
        _ = LoadIconAsync();
    }

    // ---- icon: the target app's own icon, or a glyph for the other targets -------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlyphVisibility))]
    private ImageSource? _icon;

    public Visibility GlyphVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;

    public string Glyph => Binding switch
    {
        { IsAppAction: true } => "",                 // mixer
        { Target: HotkeyTarget.App } => "",          // app (until its icon loads)
        { Target: HotkeyTarget.ActiveWindow } => "", // windows
        { Target: HotkeyTarget.MediaPlayer } => "",  // note
        { IsVolumeAction: true } => "",              // speaker: master volume
        _ => "",                                     // current media
    };

    private async Task LoadIconAsync()
    {
        var binding = Binding;
        string? path = binding.Target == HotkeyTarget.App && !binding.IsAppAction && !string.IsNullOrEmpty(binding.AppName)
            ? App.Instance.FindExePath(binding.AppName) ?? binding.AppPath
            : null;
        // Remember a running app's exe (bindings made before paths were kept): saved with the next change.
        if (path != null && binding.Target == HotkeyTarget.App) binding.AppPath ??= path;
        var icon = await AppIconLoader.LoadAsync(path);
        if (ReferenceEquals(binding, Binding)) Icon = icon; // still the same binding
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
        _ = LoadIconAsync();
    }

    public static string Describe(HotkeyBinding b)
    {
        string trigger = b.Trigger switch
        {
            TriggerKind.Double => "Double press",
            TriggerKind.Triple => "Triple press",
            TriggerKind.Hold => b.Repeats ? "Hold (repeats)" : "Hold",
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
