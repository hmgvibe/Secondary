namespace Secondary.Core;

public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey, string KeyName)
{
    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        var parts = text?.Split('+', StringSplitOptions.TrimEntries);
        if (parts is null || parts.Length < 2) return false;
        uint mods = 0;
        foreach (var part in parts[..^1])
        {
            uint bit = part.ToUpperInvariant() switch { "CTRL" => 2, "ALT" => 1, "SHIFT" => 4, _ => 0 };
            if (bit == 0 || (mods & bit) != 0) return false;
            mods |= bit;
        }
        if ((mods & 3) == 0) return false;
        var key = parts[^1].ToUpperInvariant();
        uint vk;
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) vk = key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 11) vk = (uint)(111 + f);
        else return false;
        gesture = new(mods, vk, key);
        return true;
    }
    public override string ToString() => ((Modifiers & 2) != 0 ? "Ctrl+" : "") + ((Modifiers & 1) != 0 ? "Alt+" : "") + ((Modifiers & 4) != 0 ? "Shift+" : "") + KeyName;
}
