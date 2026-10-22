using CommunityToolkit.Mvvm.ComponentModel;

namespace Omnideck.ViewModels;

/// <summary>One kind of on-screen notice on the OSD page: a tile that switches it on or off.</summary>
public sealed partial class OsdKindTile(string title, string caption, string glyph, Action changed) : ObservableObject
{
    public string Title { get; } = title;
    public string Caption { get; } = caption;
    public string Glyph { get; } = glyph;

    [ObservableProperty]
    private bool _isOn;

    /// <summary>Set without reporting a change (when the page loads the saved settings).</summary>
    public void Load(bool on)
    {
        _loading = true;
        IsOn = on;
        _loading = false;
    }

    private bool _loading;

    partial void OnIsOnChanged(bool value)
    {
        if (!_loading) changed();
    }
}
