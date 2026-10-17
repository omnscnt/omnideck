using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Omnisound.Core.Hotkeys;
using Omnisound.ViewModels;

namespace Omnisound.Views;

/// <summary>
/// Inline editor shown inside an expanded hotkey card. Edits a copy of the binding and commits it
/// (applied and saved) whenever it is valid: has keys, doesn't clash with another hotkey, and names
/// an app when the target is a specific app. Invalid states only show why.
/// </summary>
public sealed partial class HotkeyEditor : UserControl
{
    private readonly HotkeyItemViewModel _item;
    private readonly HotkeyBinding _draft;
    private bool _recording;
    private bool _loading;

    public HotkeyEditor(HotkeyItemViewModel item)
    {
        _item = item;
        _draft = item.Binding.Clone();
        InitializeComponent();

        _loading = true;
        // Apps playing sound now (plus the bound one), each with its icon.
        var names = item.Owner.AppNames().Append(_draft.AppName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n);
        foreach (var name in names)
            AppBox.Items.Add(new WidgetAppItem(name, App.Instance.FindExePath(name) ?? (name == _draft.AppName ? _draft.AppPath : null)));
        TriggerBox.SelectedIndex = (int)_draft.Trigger;
        ActionBox.SelectedIndex = (int)_draft.Action;
        TargetBox.SelectedIndex = (int)_draft.Target;
        StepBox.Value = _draft.Step;
        if (_draft.AppName != null) AppBox.SelectedItem = AppBox.Items.OfType<WidgetAppItem>().FirstOrDefault(a => a.Name == _draft.AppName);
        BlockBox.IsChecked = _draft.BlockKey;
        _loading = false;

        Refresh(commit: false);
    }

    public void StartRecording()
    {
        if (_recording) return;
        _recording = true;
        Refresh(commit: false);
        _item.Owner.Engine.Record(chord => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_recording) return;
            _recording = false;
            if (!chord.IsEmpty) _draft.Keys = chord;
            Refresh(commit: true);
        }));
    }

    public void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _item.Owner.Engine.CancelRecord();
        Refresh(commit: false);
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();
        else StartRecording();
    }

    private void OnFieldChanged(object sender, SelectionChangedEventArgs e) => Refresh(commit: true);

    private void OnStepChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Refresh(commit: true);

    private void OnBlockClick(object sender, RoutedEventArgs e) => Refresh(commit: true);

    private void OnAppTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args) => Refresh(commit: true);

    private void OnAppKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) => Refresh(commit: true);

    private void ReadFields()
    {
        _draft.Trigger = (TriggerKind)Math.Max(0, TriggerBox.SelectedIndex);
        _draft.Action = (HotkeyAction)Math.Max(0, ActionBox.SelectedIndex);
        _draft.Target = (HotkeyTarget)Math.Max(0, TargetBox.SelectedIndex);
        _draft.Step = double.IsNaN(StepBox.Value) ? 5 : (int)Math.Round(StepBox.Value);
        string app = (AppBox.SelectedItem?.ToString() ?? AppBox.Text ?? "").Trim();
        string? previous = _draft.AppName;
        _draft.AppName = app.Length == 0 ? null : Path.GetFileNameWithoutExtension(app).ToLowerInvariant();
        if (_draft.AppName != previous || _draft.AppPath == null)
            _draft.AppPath = _draft.AppName == null ? null
                : (AppBox.SelectedItem as WidgetAppItem)?.ExePath ?? App.Instance.FindExePath(_draft.AppName) ?? (_draft.AppName == previous ? _draft.AppPath : null);
        _draft.BlockKey = BlockBox.IsChecked == true;
    }

    private void Refresh(bool commit)
    {
        if (_loading) return;
        ReadFields();

        RecordText.Text = _recording
            ? "Press a key combination…  (Esc to cancel)"
            : _draft.Keys.IsEmpty ? "Click to record a shortcut" : _draft.Keys.ToString();
        RecordIcon.Glyph = _recording ? "" : ""; // record dot / keyboard

        StepBox.Visibility = _draft.Repeats ? Visibility.Visible : Visibility.Collapsed;
        TargetBox.Visibility = _draft.IsAppAction ? Visibility.Collapsed : Visibility.Visible;
        AppBox.Visibility = !_draft.IsAppAction && _draft.Target == HotkeyTarget.App ? Visibility.Visible : Visibility.Collapsed;
        // Keep the app picker in its column when the step box is hidden, and drop the row when both are.
        ExtraRow.Visibility = StepBox.Visibility == Visibility.Visible || AppBox.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

        var conflict = _draft.Keys.IsEmpty ? null : _item.Owner.FindConflict(_draft);
        bool appMissing = !_draft.IsAppAction && _draft.Target == HotkeyTarget.App && string.IsNullOrEmpty(_draft.AppName);
        string? problem = conflict != null ? $"Already used: {HotkeyItemViewModel.Describe(conflict)}. Not saved."
            : appMissing ? "Pick or type an app. Not saved."
            : null;
        StatusText.Text = problem ?? "";
        StatusText.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;

        if (commit && !_recording && problem == null && !_draft.Keys.IsEmpty)
        {
            _draft.Enabled = _item.Binding.Enabled; // toggled in the card header, not here
            _item.Owner.Update(_item, _draft.Clone());
        }
    }
}
