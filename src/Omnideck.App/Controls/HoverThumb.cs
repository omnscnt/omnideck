using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Omnideck.Controls;

/// <summary>
/// Shows a slider's thumb only while it is in use: the pointer over the slider, a drag (which may leave
/// it), or keyboard use. The rest of the time the slider is just its track, like a level bar. Fades.
/// </summary>
public static class HoverThumb
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(HoverThumb), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(Slider slider) => (bool)slider.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Slider slider, bool value) => slider.SetValue(IsEnabledProperty, value);

    private sealed class State
    {
        public bool Hovered, Dragging, Keys;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(HoverThumb), new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider || e.NewValue is not true || slider.GetValue(StateProperty) != null) return;
        var state = new State();
        slider.SetValue(StateProperty, state);

        slider.Loaded += (_, _) =>
        {
            AlignFillToTrack(slider);
            Update(slider, animate: false);
        };
        slider.PointerEntered += (_, _) => { state.Hovered = true; Update(slider); };
        slider.PointerExited += (_, _) => { state.Hovered = false; Update(slider); };
        // A press may start a drag that leaves the slider: keep the thumb until it ends.
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { state.Dragging = true; Update(slider); }), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { state.Dragging = false; Update(slider); }), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => { state.Dragging = false; Update(slider); }), true);
        // Keyboard: once arrows actually move it (the window hands it focus on opening, which shouldn't count).
        slider.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => { state.Keys = true; Update(slider); }), true);
        slider.LostFocus += (_, _) => { state.Keys = false; Update(slider); };
    }

    private static void Update(Slider slider, bool animate = true)
    {
        if (slider.GetValue(StateProperty) is not State state || FindThumb(slider) is not Thumb thumb) return;
        bool show = state.Hovered || state.Dragging || state.Keys;
        thumb.OpacityTransition = animate ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) } : null;
        thumb.Opacity = show ? 1 : 0;
    }

    /// <summary>
    /// The template's fill ends at the thumb's left edge and the track runs the full width, so without a
    /// visible thumb 100% stops short of the end. Inset the track by half a thumb on both sides and move
    /// the fill half a thumb right: it then runs from the track's start to the thumb's centre, 0% to 100%.
    /// </summary>
    private static void AlignFillToTrack(Slider slider)
    {
        if (FindThumb(slider) is not Thumb thumb) return;
        double half = thumb.ActualWidth > 0 ? thumb.ActualWidth / 2 : thumb.Width / 2;
        if (double.IsNaN(half) || half <= 0) return;
        if (FindNamed(slider, "HorizontalTrackRect") is FrameworkElement track) track.Margin = new Thickness(half, 0, half, 0);
        if (FindNamed(slider, "HorizontalDecreaseRect") is FrameworkElement fill) fill.RenderTransform = new TranslateTransform { X = half };
    }

    private static FrameworkElement? FindNamed(DependencyObject parent, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindNamed(child, name) is { } deeper) return deeper;
        }
        return null;
    }

    private static Thumb? FindThumb(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Thumb { Name: "HorizontalThumb" } thumb) return thumb;
            if (FindThumb(child) is { } deeper) return deeper;
        }
        return null;
    }
}
