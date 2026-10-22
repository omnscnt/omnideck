using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omnideck.Core.Hotkeys;

namespace Omnideck.ViewModels;

/// <summary>The editable hotkey list; every change (including order) is applied and saved immediately.</summary>
public sealed partial class HotkeysViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<HotkeyBinding>> _apply;

    public ObservableCollection<HotkeyItemViewModel> Items { get; } = [];

    /// <summary>Used by the inline editor to record shortcuts.</summary>
    public HotkeyEngine Engine { get; }

    /// <summary>Process names of apps currently playing sound, for the app picker.</summary>
    public Func<IEnumerable<string>> AppNames { get; }

    public HotkeysViewModel(IEnumerable<HotkeyBinding> bindings, Action<IReadOnlyList<HotkeyBinding>> apply, HotkeyEngine engine, Func<IEnumerable<string>> appNames)
    {
        _apply = apply;
        Engine = engine;
        AppNames = appNames;
        foreach (var b in bindings) Items.Add(new HotkeyItemViewModel(b, this));
    }

    /// <summary>
    /// Inserts a blank hotkey at the top (under the add card) that opens for editing. It is not saved until it has keys, and is
    /// dropped if it is collapsed without any.
    /// </summary>
    public HotkeyItemViewModel AddDraft()
    {
        var item = new HotkeyItemViewModel(new HotkeyBinding(), this) { IsNew = true, PendingExpand = true };
        Items.Insert(0, item); // right under the add card
        return item;
    }

    public void Update(HotkeyItemViewModel item, HotkeyBinding binding)
    {
        item.Replace(binding);
        item.IsNew = false;
        Save();
    }

    public void Remove(HotkeyItemViewModel item)
    {
        Items.Remove(item);
        Save();
    }

    /// <summary>Another binding already using the same keys and trigger (they'd fight), if any.</summary>
    public HotkeyBinding? FindConflict(HotkeyBinding candidate) =>
        Items.Select(i => i.Binding).FirstOrDefault(b =>
            b.Id != candidate.Id && b.Enabled && b.Keys == candidate.Keys && b.Trigger == candidate.Trigger);

    public void Save() => _apply(Items.Select(i => i.Binding).Where(b => !b.Keys.IsEmpty).ToList());
}
