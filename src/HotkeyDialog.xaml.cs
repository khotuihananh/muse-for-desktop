using System;
using System.Windows;
using System.Windows.Input;

namespace MuseDesktop;

/// <summary>
/// Assign-any-key dialog for the three capture actions. Click "Đổi", then
/// press the key combination: it is captured from the dialog's own
/// PreviewKeyDown (the host suspends its keyboard hook while this
/// dialog is open, so the old combo can neither fire nor be swallowed
/// behind its back; saved bindings go live the moment it closes).
/// Since v1.6.1 any single key is accepted as a binding, per the user.
/// </summary>
public partial class HotkeyDialog : Window
{
    private string? _armed; // "lens" | "screen1" | "screen2"

    public HotkeyDialog()
    {
        InitializeComponent();
        Refresh();
        BtnSetLens.Click += (_, _) => Arm("lens", "Len");
        BtnSetScreen1.Click += (_, _) => Arm("screen1", "Màn hình 1");
        BtnSetScreen2.Click += (_, _) => Arm("screen2", "Màn hình 2");
        BtnSetRec1.Click += (_, _) => Arm("rec1", "Quay màn hình 1");
        BtnSetRec2.Click += (_, _) => Arm("rec2", "Quay màn hình 2");
        BtnClearLens.Click += (_, _) => Clear(HotkeySettings.Lens);
        BtnClearScreen1.Click += (_, _) => Clear(HotkeySettings.Screen1);
        BtnClearScreen2.Click += (_, _) => Clear(HotkeySettings.Screen2);
        BtnClearRec1.Click += (_, _) => Clear(HotkeySettings.Rec1);
        BtnClearRec2.Click += (_, _) => Clear(HotkeySettings.Rec2);
        BtnClose.Click += (_, _) => Close();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Refresh()
    {
        BindLens.Text = HotkeySettings.Lens.Display;
        BindScreen1.Text = HotkeySettings.Screen1.Display;
        BindScreen2.Text = HotkeySettings.Screen2.Display;
        BindRec1.Text = HotkeySettings.Rec1.Display;
        BindRec2.Text = HotkeySettings.Rec2.Display;
    }

    private void Arm(string which, string label)
    {
        _armed = which;
        Hint.Text = $"Đang chờ phím cho {label}… gõ tổ hợp phím muốn dùng, Esc để hủy.";
    }

    private void Clear(HotkeyBinding b)
    {
        b.Mods = 0;
        b.Vk = 0;
        HotkeySettings.Save();
        _armed = null;
        Hint.Text = "";
        Refresh();
    }

    private HotkeyBinding? Target() => _armed switch
    {
        "lens" => HotkeySettings.Lens,
        "screen1" => HotkeySettings.Screen1,
        "screen2" => HotkeySettings.Screen2,
        "rec1" => HotkeySettings.Rec1,
        "rec2" => HotkeySettings.Rec2,
        _ => null,
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_armed == null) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _armed = null;
            Hint.Text = "";
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Modifier-only presses just build the combo; wait for the real key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        uint mods = 0;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) mods |= Native.MOD_CONTROL;
        if ((m & ModifierKeys.Alt) != 0) mods |= Native.MOD_ALT;
        if ((m & ModifierKeys.Shift) != 0) mods |= Native.MOD_SHIFT;
        if ((m & ModifierKeys.Windows) != 0) mods |= Native.MOD_WIN;

        // v1.6.1, at the user's explicit order: ANY single key is a valid
        // binding — no modifier/F-key gate. A bare letter as a global
        // hotkey swallows that letter system-wide while the app runs;
        // the dialog's intro text says so, and the choice is his.
        var target = Target();
        if (target == null) { _armed = null; return; }
        target.Mods = mods;
        target.Vk = vk;
        HotkeySettings.Save();
        _armed = null;
        Hint.Text = "Đã gán: " + target.Display;
        Refresh();
    }
}
