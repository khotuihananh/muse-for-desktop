using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MuseDesktop;

/// <summary>One assignable global hotkey (modifiers + virtual key code).</summary>
public sealed class HotkeyBinding
{
    public uint Mods { get; set; }
    public uint Vk { get; set; }
    public bool IsSet => Vk != 0;

    public string Display
    {
        get
        {
            if (!IsSet) return "chưa gán";
            var parts = new List<string>();
            if ((Mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Mods & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((Mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((Mods & Native.MOD_WIN) != 0) parts.Add("Win");
            parts.Add(HotkeySettings.KeyName(Vk));
            return string.Join("+", parts);
        }
    }
}

/// <summary>
/// The three capture hotkeys, persisted at
/// %LocalAppData%\MuseDesktop\hotkeys.json. Defaults: Len = Ctrl+Alt+V,
/// Màn hình 1 = Ctrl+Alt+1, Màn hình 2 = Ctrl+Alt+2.
/// </summary>
public static class HotkeySettings
{
    public static HotkeyBinding Lens { get; private set; } =
        new() { Mods = Native.MOD_CONTROL | Native.MOD_ALT, Vk = 0x56 }; // V
    public static HotkeyBinding Screen1 { get; private set; } =
        new() { Mods = Native.MOD_CONTROL | Native.MOD_ALT, Vk = 0x31 }; // 1
    public static HotkeyBinding Screen2 { get; private set; } =
        new() { Mods = Native.MOD_CONTROL | Native.MOD_ALT, Vk = 0x32 }; // 2
    // Recording toggles ship unassigned: the user picks their own keys.
    public static HotkeyBinding Rec1 { get; private set; } = new();
    public static HotkeyBinding Rec2 { get; private set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseDesktop", "hotkeys.json");

    private sealed class Dto
    {
        public HotkeyBinding? Lens { get; set; }
        public HotkeyBinding? Screen1 { get; set; }
        public HotkeyBinding? Screen2 { get; set; }
        public HotkeyBinding? Rec1 { get; set; }
        public HotkeyBinding? Rec2 { get; set; }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
            if (dto == null) return;
            if (dto.Lens != null) Lens = dto.Lens;
            if (dto.Screen1 != null) Screen1 = dto.Screen1;
            if (dto.Screen2 != null) Screen2 = dto.Screen2;
            if (dto.Rec1 != null) Rec1 = dto.Rec1;
            if (dto.Rec2 != null) Rec2 = dto.Rec2;
        }
        catch { /* Corrupt settings file: keep the defaults. */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new Dto { Lens = Lens, Screen1 = Screen1, Screen2 = Screen2, Rec1 = Rec1, Rec2 = Rec2 }));
        }
        catch { /* Non-fatal: bindings still apply for this session. */ }
    }

    public static string KeyName(uint vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();       // 0-9
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();       // A-Z
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);           // F1-F24
        return vk switch
        {
            0x20 => "Space",
            0x2C => "PrintScreen",
            0x13 => "Pause",
            0x91 => "ScrollLock",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x09 => "Tab",
            0x0D => "Enter",
            0x08 => "Backspace",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            _ => "VK " + vk,
        };
    }
}
