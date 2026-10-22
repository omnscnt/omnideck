using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Omnideck.Services;

namespace Omnideck.ViewModels;

/// <summary>An app in the taskbar widget's allow / block list (or in the picker), with its icon.</summary>
public sealed partial class WidgetAppItem : ObservableObject
{
    public string Name { get; }

    /// <summary>The app's exe, when known: running now, or remembered from when it was added.</summary>
    public string? ExePath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FallbackVisibility))]
    private ImageSource? _icon;

    /// <summary>A generic app glyph stands in while there is no icon.</summary>
    public Visibility FallbackVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;

    public WidgetAppItem(string name, string? exePath)
    {
        Name = name;
        ExePath = exePath;
        _ = LoadIconAsync();
    }

    private async Task LoadIconAsync() => Icon = await AppIconLoader.LoadAsync(ExePath);

    /// <summary>What the editable picker shows in its text box for a chosen item.</summary>
    public override string ToString() => Name;
}
