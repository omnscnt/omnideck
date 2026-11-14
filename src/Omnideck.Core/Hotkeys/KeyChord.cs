using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace Omnideck.Core.Hotkeys;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>An unordered combination of virtual keys and modifiers.</summary>
public readonly record struct KeyChord
{
    public KeyModifiers Modifiers { get; }
    public int Key { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? AdditionalKeys { get; }

    [JsonConstructor]
    public KeyChord(KeyModifiers modifiers, int key, IReadOnlyList<int>? additionalKeys = null)
    {
        Modifiers = modifiers;
        var keys = (additionalKeys ?? []).Append(key).Where(vk => vk != 0).Distinct().Order().ToArray();
        Key = keys.FirstOrDefault();
        AdditionalKeys = keys.Length > 1 ? Array.AsReadOnly(keys[1..]) : null;
    }

    [JsonIgnore]
    public bool IsEmpty => Key == 0 && Modifiers == KeyModifiers.None;

    internal int KeyCount => (Key == 0 ? 0 : 1) + (AdditionalKeys?.Count ?? 0);

    internal bool ContainsKey(int vk) => Key == vk || AdditionalKeys?.Contains(vk) == true;

    internal bool KeysAreDown(HashSet<int> pressed) =>
        (Key == 0 || pressed.Contains(Key)) && (AdditionalKeys?.All(pressed.Contains) ?? true);

    internal bool Extends(KeyChord shorter) =>
        (Modifiers & shorter.Modifiers) == shorter.Modifiers &&
        (Modifiers != shorter.Modifiers || KeyCount > shorter.KeyCount) &&
        (shorter.Key == 0 || ContainsKey(shorter.Key)) &&
        (shorter.AdditionalKeys?.All(ContainsKey) ?? true);

    public bool Equals(KeyChord other) => Modifiers == other.Modifiers && Key == other.Key
        && (AdditionalKeys ?? []).SequenceEqual(other.AdditionalKeys ?? []);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Modifiers);
        hash.Add(Key);
        foreach (int key in AdditionalKeys ?? []) hash.Add(key);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return string.Join(" + ", Parts);
    }

    /// <summary>The individual key caps, for rendering the chord as separate key boxes.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
            if (Key != 0) parts.Add(KeyName(Key));
            foreach (int key in AdditionalKeys ?? []) parts.Add(KeyName(key));
            return parts;
        }
    }

    internal static KeyModifiers ModifierFor(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => KeyModifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => KeyModifiers.Alt,
        0x10 or 0xA0 or 0xA1 => KeyModifiers.Shift,
        0x5B or 0x5C => KeyModifiers.Win,
        _ => KeyModifiers.None,
    };

    public static string KeyName(int vk)
    {
        switch (vk)
        {
            case >= 0x30 and <= 0x39: // 0-9
            case >= 0x41 and <= 0x5A: // A-Z
                return ((char)vk).ToString();
            case >= 0x70 and <= 0x87: return $"F{vk - 0x6F}";
            case >= 0x60 and <= 0x69: return $"Num {vk - 0x60}";
            case 0xAD: return "Volume Mute";
            case 0xAE: return "Volume Down";
            case 0xAF: return "Volume Up";
            case 0xB0: return "Next Track";
            case 0xB1: return "Previous Track";
            case 0xB2: return "Stop";
            case 0xB3: return "Play/Pause";
            case 0x20: return "Space";
            case 0x0D: return "Enter";
            case 0x1B: return "Esc";
            case 0x09: return "Tab";
            case 0x08: return "Backspace";
            case 0x2D: return "Insert";
            case 0x2E: return "Delete";
            case 0x24: return "Home";
            case 0x23: return "End";
            case 0x21: return "Page Up";
            case 0x22: return "Page Down";
            case 0x25: return "Left";
            case 0x26: return "Up";
            case 0x27: return "Right";
            case 0x28: return "Down";
            case 0x2C: return "Print Screen";
            case 0x91: return "Scroll Lock";
            case 0x13: return "Pause";
            case 0x6A: return "Num *";
            case 0x6B: return "Num +";
            case 0x6D: return "Num -";
            case 0x6E: return "Num .";
            case 0x6F: return "Num /";
        }

        // Punctuation and anything layout-specific: ask Windows for the key's label.
        uint scan = MapVirtualKey((uint)vk, 0 /* MAPVK_VK_TO_VSC */);
        if (scan != 0)
        {
            var name = new StringBuilder(64);
            if (GetKeyNameText((int)(scan << 16), name, name.Capacity) > 0) return name.ToString();
        }
        return $"Key 0x{vk:X2}";
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int lParam, StringBuilder name, int size);
}
