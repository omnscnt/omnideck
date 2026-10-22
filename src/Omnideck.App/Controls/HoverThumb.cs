using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Omnideck.Controls;

/// <summary>
/// Shows a slider's thumb only while it is in use: the pointer over the slider, a drag (which may leave
/// it), or keyboard focus. The rest of the time the slider is just its track, like a level bar. Fades.
/// </summary>
public static class HoverThumb
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(HoverThumb), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(Slider slider) => (bool)slider.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Slider slider, bool value) => slider.SetValue(IsEnabledProperty, value);

    private sealed class State
    {
        public bool Hovered, Dragging;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(HoverThumb), new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider || e.NewValue is not true || slider.GetValue(StateProperty) != null) return;
        var state = new State();
        slider.SetValue(StateProperty, state);

        slider.Loaded += (_, _) => Update(slider, animate: false);
        slider.PointerEntered += (_, _) => { state.Hovered = true; Update(slider); };
        slider.PointerExited += (_, _) => { state.Hovered = false; Update(slider); };
        // A press may start a drag that leaves the slider: keep the thumb until it ends.
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { state.Dragging = true; Update(slider); }), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { state.Dragging = false; Update(slider); }), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => { state.Dragging = false; Update(slider); }), true);
        slider.GotFocus += (_, _) => Update(slider);
        slider.LostFocus += (_, _) => Update(slider);
    }

    private static void Update(Slider slider, bool animate = true)
    {
        if (slider.GetValue(StateProperty) is not State state || FindThumb(slider) is not Thumb thumb) return;
        bool show = state.Hovered || state.Dragging || slider.FocusState == FocusState.Keyboard;
        thumb.OpacityTransition = animate ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) } : null;
        thumb.Opacity = show ? 1 : 0;
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
