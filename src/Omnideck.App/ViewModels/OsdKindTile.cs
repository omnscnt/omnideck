using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Omnideck.ViewModels;

/// <summary>
/// One kind of on-screen notice on the OSD page: a tile that switches it on or off. Its icon is a
/// Fluent glyph, or (for keys) a key cap with a label, like the notice itself shows.
/// </summary>
public sealed partial class OsdKindTile(string title, string caption, string? glyph, string? keyLabel, Action changed) : ObservableObject
{
    public string Title { get; } = title;
    public string Caption { get; } = caption;
    public string Glyph { get; } = glyph ?? "";
    public string KeyLabel { get; } = keyLabel ?? "";

    public Visibility GlyphVisibility => glyph != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KeyVisibility => keyLabel != null ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private bool _isOn;

    private bool _loading;

    /// <summary>Set without reporting a change (when the page loads the saved settings).</summary>
    public void Load(bool on)
    {
        _loading = true;
        IsOn = on;
        _loading = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_loading) changed();
    }
}
