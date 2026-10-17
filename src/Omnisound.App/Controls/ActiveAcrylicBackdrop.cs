using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Omnisound.Controls;

/// <summary>
/// Desktop acrylic that never falls back to the solid "inactive" fill. Needed for popups:
/// a windowed MenuFlyout never owns activation itself, so the stock backdrop would render it opaque.
/// </summary>
public sealed class ActiveAcrylicBackdrop : SystemBackdrop
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, DesktopAcrylicController> _controllers = [];

    public DesktopAcrylicKind Kind { get; init; } = DesktopAcrylicKind.Base;

    /// <summary>Optional overrides of the kind's preset; lower opacities = more see-through.</summary>
    public Windows.UI.Color? TintColor { get; init; }
    public float? TintOpacity { get; init; }
    public float? LuminosityOpacity { get; init; }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;

        var config = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = (xamlRoot.Content as FrameworkElement)?.ActualTheme switch
            {
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default,
            },
        };
        var controller = new DesktopAcrylicController { Kind = Kind };
        // The tint override is a dark-theme colour; light theme keeps the preset tint.
        if (TintColor is { } tint && config.Theme != SystemBackdropTheme.Light) controller.TintColor = tint;
        if (TintOpacity is { } tintOpacity) controller.TintOpacity = tintOpacity;
        if (LuminosityOpacity is { } luminosity) controller.LuminosityOpacity = luminosity;
        controller.AddSystemBackdropTarget(connectedTarget);
        controller.SetSystemBackdropConfiguration(config);
        _controllers[connectedTarget] = controller;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_controllers.Remove(disconnectedTarget, out var controller))
        {
            controller.RemoveSystemBackdropTarget(disconnectedTarget);
            controller.Dispose();
        }
    }
}
