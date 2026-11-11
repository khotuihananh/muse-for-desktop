using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace MuseDesktop;

public partial class MainWindow : Window
{
    private const string HomeUrl = "https://muse.ai";

    private bool _webReady;

    private IntPtr _hwnd;
    private readonly List<CoreWebView2Frame> _frames = new();
    private bool _chromeHidden;
    private readonly Recorder _recorder = new();
    private Native.LowLevelKeyboardProc? _kbProc; // kept alive: the hook dies with it
    private IntPtr _kbHook = IntPtr.Zero;
    private volatile bool _hotkeysSuspended; // true while the assign dialog listens
    private uint _firedVk; // key currently held after firing (auto-repeat guard)
    private readonly System.Windows.Threading.DispatcherTimer _recTimer =
        new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MainWindow()
    {
        InitializeComponent();
        // The build number rides in the title bar so any screenshot of the
        // app identifies exactly which build produced it.
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v != null) Title = $"Muse — Muse Desktop {v.Major}.{v.Minor}.{v.Build}";
        BtnLens.Click += (_, _) => _ = CaptureRegionAndPaste();
        BtnScreen1.Click += (_, _) => _ = CaptureScreenAndPaste(0);
        BtnScreen2.Click += (_, _) => _ = CaptureScreenAndPaste(1);
        BtnLens.MouseRightButtonUp += (_, _) => OpenHotkeyDialog();
        BtnScreen1.MouseRightButtonUp += (_, _) => OpenHotkeyDialog();
        BtnScreen2.MouseRightButtonUp += (_, _) => OpenHotkeyDialog();
        BtnRec1.Click += (_, _) => _ = ToggleRecord(0);
        BtnRec2.Click += (_, _) => _ = ToggleRecord(1);
        BtnRec1.MouseRightButtonUp += (_, _) => OpenHotkeyDialog();
        BtnRec2.MouseRightButtonUp += (_, _) => OpenHotkeyDialog();
        _recTimer.Tick += (_, _) => RecTimerTick();
        if (System.Windows.Forms.Screen.AllScreens.Length < 2)
            BtnScreen2.IsEnabled = false;
        HotkeySettings.Load();
        UpdateHotkeyHints();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ hotkeys

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _hwnd = handle;
        Native.TryDarkTitleBar(handle);
        InstallKeyboardHook();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        if (_kbHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_kbHook);
            _kbHook = IntPtr.Zero;
        }
        if (_recorder.IsRecording)
        {
            // Graceful stop so the MP4 tail is finalised, bounded wait:
            // app exit must not hang on a stuck encoder.
            try { _recorder.StopAsync().Wait(1500); } catch { /* Exiting anyway. */ }
        }
        base.OnClosed(e);
    }

    // ------------------------------------------------------- hotkey engine

    /// <summary>
    /// v1.6.4: the global hotkeys run on a WH_KEYBOARD_LL hook instead of
    /// RegisterHotKey. On the user's machine RegisterHotKey assignments
    /// saved correctly but WM_HOTKEY never arrived, with no visible
    /// failure. The hook sees every key press before the system turns it
    /// into anything else, matches it against the LIVE bindings, fires
    /// the action on the UI thread and swallows the key. There is no
    /// per-key registration step: if the hook is installed — checked
    /// once at startup and written to crash.log if it is not — every
    /// assigned key works, effective the moment the dialog closes.
    /// </summary>
    private void InstallKeyboardHook()
    {
        try
        {
            _kbProc = KeyboardHookCallback;
            _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc,
                Native.GetModuleHandle(null), 0);
            if (_kbHook == IntPtr.Zero)
                CrashLog.WriteNote("HotkeyHook",
                    "SetWindowsHookEx failed, error " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            CrashLog.Write("InstallKeyboardHook", ex);
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_hotkeysSuspended)
        {
            int msg = wParam.ToInt32();
            var kb = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
            {
                if (_firedVk == kb.vkCode) _firedVk = 0;
            }
            else if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
            {
                uint mods = CurrentMods();
                Action? fire = null;
                if (kb.vkCode == Native.VK_SPACE && mods == Native.MOD_ALT)
                    fire = () => ToggleShow();
                else if (Matches(HotkeySettings.Lens, kb.vkCode, mods))
                    fire = () => _ = CaptureRegionAndPaste();
                else if (Matches(HotkeySettings.Screen1, kb.vkCode, mods))
                    fire = () => _ = CaptureScreenAndPaste(0);
                else if (Matches(HotkeySettings.Screen2, kb.vkCode, mods))
                    fire = () => _ = CaptureScreenAndPaste(1);
                else if (Matches(HotkeySettings.Rec1, kb.vkCode, mods))
                    fire = () => _ = ToggleRecord(0);
                else if (Matches(HotkeySettings.Rec2, kb.vkCode, mods))
                    fire = () => _ = ToggleRecord(1);

                if (fire != null)
                {
                    bool repeat = _firedVk == kb.vkCode;
                    _firedVk = kb.vkCode;
                    if (!repeat) Dispatcher.BeginInvoke(fire);
                    return (IntPtr)1; // swallow: this key belongs to the app now
                }
            }
        }
        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private static bool Matches(HotkeyBinding b, uint vk, uint mods)
        => b.IsSet && b.Vk == vk && b.Mods == mods;

    private static uint CurrentMods()
    {
        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= Native.MOD_CONTROL; // VK_CONTROL
        if ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= Native.MOD_ALT;     // VK_MENU
        if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= Native.MOD_SHIFT;   // VK_SHIFT
        if ((Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
            (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0) mods |= Native.MOD_WIN;    // VK_LWIN/RWIN
        return mods;
    }

    // ------------------------------------------------------------------ window

    public void ToggleShow(bool forceShow = false)
    {
        if (!forceShow && IsVisible && IsActive)
        {
            Hide();
            return;
        }
        ShowAndFocus();
    }

    private bool _isClosed;

    private void ShowAndFocus()
    {
        if (_isClosed) return;
        try
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            try { Web.Focus(); } catch { /* WebView may not be initialised yet. */ }
        }
        catch (InvalidOperationException)
        {
            // The window is mid-close (e.g. X clicked in the same instant a
            // capture flow or the tray asked to show it): WPF forbids Show()
            // while closing. Retry once on the next dispatcher pass, after
            // the close has been cancelled and the window parked in the tray.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosed || IsVisible) return;
                try { ShowAndFocus(); }
                catch (Exception ex) { CrashLog.Write("ShowAndFocus-retry", ex); }
            }));
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (!app.IsExiting)
        {
            // The X button parks the app in the tray instead of quitting it,
            // like the Google app: it stays resident for Alt+Space.
            e.Cancel = true;
            Hide();
        }
    }

    // ------------------------------------------------------------------ web

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuseDesktop", "WebView2Data");
            Directory.CreateDirectory(dataFolder);

            var envOptions = new CoreWebView2EnvironmentOptions
            {
                // The composer is Vietnamese; the embedded Chromium's
                // spell checker only carries its UI-language dictionary
                // and underlines nearly every word in red. The user
                // asked for spell checking OFF (2026-11-11) — the bridge
                // also forces spellcheck=false on editables as a second
                // layer in case the switch is ever ignored.
                AdditionalBrowserArguments = "--disable-spell-checking",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, envOptions);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Web.CoreWebView2.ContextMenuRequested += OnContextMenuRequested;
            Web.CoreWebView2.FrameCreated += OnFrameCreated;
            // A small helper script lives inside the chat page (see
            // BridgeScript): it only knows how to find and focus the
            // composer, which the capture flows use right before pasting.
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
            Web.Source = new Uri(HomeUrl);
            _webReady = true;
        }
        catch (Exception ex)
        {
            FallbackText.Text =
                "Không khởi động được trình duyệt nhúng (WebView2).\n\n" +
                "Chi tiết: " + ex.Message + "\n\n" +
                "Hãy cài Microsoft Edge WebView2 Runtime (máy Windows 10/11 thường đã có sẵn qua Edge) rồi mở lại app.";
            FallbackText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The chat composer may live inside an iframe rather than the top
    /// document. Track every frame and (re)inject the helper script after
    /// each frame navigation, so the composer can be focused there too.
    /// </summary>
    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        var frame = e.Frame;
        _frames.Add(frame);
        frame.NavigationCompleted += async (_, _) =>
        {
            try
            {
                await frame.ExecuteScriptAsync(BridgeScript);
            }
            catch { /* Frame not scriptable (yet) or already gone. */ }
        };
    }

    private async Task FocusComposerAsync()
    {
        if (!_webReady) return;
        // Prefer the bridge's own composer search (shadow-DOM aware, lowest
        // visible editable wins); fall back to a plain selector sweep.
        if (await EvalBoolAsync("window.__museFocusComposer && window.__museFocusComposer()"))
            return;
        foreach (var frame in _frames.ToArray())
        {
            try
            {
                string r = await frame.ExecuteScriptAsync(
                    "window.__museFocusComposer && window.__museFocusComposer()");
                if ((r ?? "").Trim() == "true") return;
            }
            catch { /* Frame gone or not scriptable. */ }
        }
        const string js = """
            (function () {
              var sels = ['div[contenteditable="true"]', '[contenteditable="true"]', 'textarea'];
              for (var i = 0; i < sels.length; i++) {
                var el = document.querySelector(sels[i]);
                if (el) { el.focus(); return 'ok'; }
              }
              return 'none';
            })()
            """;
        try { await Web.CoreWebView2.ExecuteScriptAsync(js); }
        catch { /* Page not ready; the paste still lands if the user clicked the composer. */ }
    }

    // ------------------------------------------------------------------ capture

    /// <summary>Capture one whole monitor and paste it into the chat composer.</summary>
    public async Task CaptureScreenAndPaste(int screenIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screenIndex < 0 || screenIndex >= screens.Length)
        {
            SetStage($"[{DateTime.Now:HH:mm:ss}] Không có màn hình {screenIndex + 1} trên máy này.");
            return;
        }
        var rect = screens[screenIndex].Bounds;

        bool wasVisible = IsVisible;
        if (wasVisible) Hide();
        await Task.Delay(wasVisible ? 300 : 40);

        var bmp = Capture.CaptureRect(rect);
        if (bmp == null) { if (wasVisible) ShowAndFocus(); return; }
        using (bmp) Capture.CopyToClipboard(bmp);

        ShowAndFocus();
        await Task.Delay(350);
        await FocusComposerAsync();
        await Task.Delay(60);
        Native.SendCtrlV();
        SetStage($"[{DateTime.Now:HH:mm:ss}] Đã dán ảnh màn hình {screenIndex + 1} vào ô chat ✓");
    }

    public async Task CaptureRegionAndPaste()
    {
        bool wasVisible = IsVisible;
        if (wasVisible) Hide();
        await Task.Delay(wasVisible ? 300 : 40);

        var picker = new RegionWindow();
        var ok = picker.ShowDialog();
        if (ok != true || picker.SelectedRect is not { } rect)
        {
            if (wasVisible) ShowAndFocus();
            return;
        }

        await Task.Delay(120); // Let the overlay fully disappear before the shot.
        var bmp = Capture.CaptureRect(rect);
        if (bmp == null) { if (wasVisible) ShowAndFocus(); return; }
        using (bmp) Capture.CopyToClipboard(bmp);

        ShowAndFocus();
        await Task.Delay(350);
        await FocusComposerAsync();
        await Task.Delay(60);
        Native.SendCtrlV();
        SetStage($"[{DateTime.Now:HH:mm:ss}] Đã dán ảnh vùng len vào ô chat ✓");
    }


    private void UpdateHotkeyHints()
    {
        BtnLens.ToolTip = "Khoanh một vùng trên màn hình, chụp rồi dán vào ô chat ("
            + HotkeySettings.Lens.Display + ") — chuột phải để đổi phím";
        BtnScreen1.ToolTip = "Chụp màn hình 1 rồi dán vào ô chat ("
            + HotkeySettings.Screen1.Display + ") — chuột phải để đổi phím";
        BtnScreen2.ToolTip = "Chụp màn hình 2 rồi dán vào ô chat ("
            + HotkeySettings.Screen2.Display + ") — chuột phải để đổi phím";
        BtnRec1.ToolTip = "Quay màn hình 1: bấm lần 1 để quay, bấm lần 2 dừng và dán video vào ô chat ("
            + HotkeySettings.Rec1.Display + ") — chuột phải để đổi phím";
        BtnRec2.ToolTip = "Quay màn hình 2: bấm lần 1 để quay, bấm lần 2 dừng và dán video vào ô chat ("
            + HotkeySettings.Rec2.Display + ") — chuột phải để đổi phím";
        string ready = "Sẵn sàng — Len: " + HotkeySettings.Lens.Display
            + " · Màn hình 1: " + HotkeySettings.Screen1.Display
            + " · Màn hình 2: " + HotkeySettings.Screen2.Display
            + " · Chuột phải vào nút để đổi phím";
        SetStage(ready);
    }

    public void OpenHotkeyDialog()
    {
        if (!IsVisible) ShowAndFocus();
        // Suspend the hook while the dialog listens for a key: the old
        // binding must neither fire nor be swallowed behind its back.
        // The hook matches the live bindings, so what the dialog saves
        // is active the moment it closes — nothing to re-register.
        _hotkeysSuspended = true;
        try
        {
            var dlg = new HotkeyDialog { Owner = this };
            dlg.ShowDialog();
        }
        finally
        {
            _hotkeysSuspended = false;
            UpdateHotkeyHints();
        }
    }

    public void ReloadPage()
    {
        try { Web.CoreWebView2?.Reload(); } catch { /* WebView not ready yet. */ }
    }

    /// <summary>Hide or show the toolbar + status bar, leaving a clean chat-only window.</summary>
    public void ToggleChrome()
    {
        _chromeHidden = !_chromeHidden;
        ToolbarGrid.Visibility = _chromeHidden ? Visibility.Collapsed : Visibility.Visible;
        StatusGrid.Visibility = _chromeHidden ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------- right-click menu

    /// <summary>
    /// The app's right-click menu. v1.6.1 built it through WebView2's
    /// ContextMenuRequested items API and it never appeared on the
    /// user's machine. v1.6.2 moves BOTH ends into layers that are
    /// proven in this app: the page hook lives in the injected bridge
    /// (capture-phase contextmenu listener → postMessage), and the menu
    /// itself is a WPF ContextMenu popup drawn by our own window — the
    /// same popup technology as the working tray menu. The WebView2
    /// handler below now only suppresses the browser's own menu so the
    /// two can never compete.
    /// </summary>
    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        try
        {
            e.MenuItems.Clear();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ContextMenuSuppress", ex);
        }
    }

    private void ShowAppContextMenu()
    {
        try
        {
            var fg = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE8, 0xE8, 0xEA));
            var menu = new System.Windows.Controls.ContextMenu
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x23, 0x23, 0x28)),
                Foreground = fg,
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x42)),
                PlacementTarget = Web,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            };
            void Add(string label, Action action)
            {
                var mi = new System.Windows.Controls.MenuItem { Header = label, Foreground = fg };
                mi.Click += (_, _) => action();
                menu.Items.Add(mi);
            }
            void Sep() => menu.Items.Add(new System.Windows.Controls.Separator());

            Add("🔍 Chụp Len", () => _ = CaptureRegionAndPaste());
            Add("🖥 Chụp màn hình 1", () => _ = CaptureScreenAndPaste(0));
            Add("🖥 Chụp màn hình 2", () => _ = CaptureScreenAndPaste(1));
            Sep();
            Add(_recorder.IsRecording && _recorder.ScreenIndex == 0
                ? "⏹ Dừng quay màn hình 1 và dán vào chat" : "⏺ Quay màn hình 1", () => _ = ToggleRecord(0));
            Add(_recorder.IsRecording && _recorder.ScreenIndex == 1
                ? "⏹ Dừng quay màn hình 2 và dán vào chat" : "⏺ Quay màn hình 2", () => _ = ToggleRecord(1));
            Sep();
            Add("⌨ Gán phím chụp…", OpenHotkeyDialog);
            Add(_chromeHidden ? "Hiện thanh công cụ" : "Ẩn thanh công cụ (chỉ còn khung chat)", ToggleChrome);
            Add("↻ Tải lại trang", ReloadPage);
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ShowAppContextMenu", ex);
        }
    }

    // ------------------------------------------------------------ recording

    /// <summary>
    /// Toggle recording of one monitor. First press starts ffmpeg on that
    /// screen; second press stops it and pastes the MP4 into the composer.
    /// </summary>
    public async Task ToggleRecord(int screenIndex)
    {
        if (_recorder.IsRecording)
        {
            if (_recorder.ScreenIndex != screenIndex)
            {
                SetStage($"Đang quay màn hình {_recorder.ScreenIndex + 1} — dừng bản quay đó trước đã.");
                return;
            }
            await StopRecordAndPaste();
            return;
        }

        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screenIndex < 0 || screenIndex >= screens.Length)
        {
            SetStage($"[{DateTime.Now:HH:mm:ss}] Không có màn hình {screenIndex + 1} trên máy này.");
            return;
        }
        string? err = _recorder.Start(screenIndex, screens[screenIndex].Bounds);
        if (err != null)
        {
            CrashLog.WriteNote("RecordStart", err + " | " + _recorder.StderrTail);
            SetStage($"[{DateTime.Now:HH:mm:ss}] Không quay được: {err}");
            return;
        }
        _recTimer.Start();
        UpdateRecordUi();
        SetStage($"[{DateTime.Now:HH:mm:ss}] ⏺ Đang quay màn hình {screenIndex + 1}… bấm Quay {screenIndex + 1} lần nữa để dừng và dán video vào chat.");
    }

    private async Task StopRecordAndPaste()
    {
        _recTimer.Stop();
        int idx = _recorder.ScreenIndex;
        var started = _recorder.StartedAt;
        string? path = await _recorder.StopAsync();
        UpdateRecordUi();
        if (path == null)
        {
            CrashLog.WriteNote("RecordStop", "no output file | " + _recorder.StderrTail);
            SetStage($"[{DateTime.Now:HH:mm:ss}] Bản quay lỗi, không có file video.");
            return;
        }
        var fi = new FileInfo(path);
        var elapsed = DateTime.Now - started;
        var elapsedText = elapsed.ToString(@"mm\:ss");
        var files = new System.Collections.Specialized.StringCollection { path };
        System.Windows.Clipboard.SetFileDropList(files);

        ShowAndFocus();
        await Task.Delay(350);
        await FocusComposerAsync();
        await Task.Delay(60);
        Native.SendCtrlV();
        SetStage($"[{DateTime.Now:HH:mm:ss}] Đã dán video màn hình {idx + 1} " +
            $"({elapsedText}, {fi.Length / 1048576.0:0.0} MB) vào ô chat ✓ — file giữ tại {path}");
    }

    private void RecTimerTick()
    {
        if (!_recorder.IsRecording)
        {
            // ffmpeg died on its own (bad device, encoder error): reset
            // the UI and say so instead of pretending to record forever.
            _recTimer.Stop();
            UpdateRecordUi();
            CrashLog.WriteNote("RecordDied", _recorder.StderrTail);
            SetStage($"[{DateTime.Now:HH:mm:ss}] Bản quay đã dừng giữa chừng (ffmpeg thoát). Chi tiết trong crash.log.");
            return;
        }
        UpdateRecordUi();
    }

    private void UpdateRecordUi()
    {
        bool rec = _recorder.IsRecording;
        int idx = _recorder.ScreenIndex;
        var elapsed = rec ? DateTime.Now - _recorder.StartedAt : TimeSpan.Zero;
        var elapsedText = elapsed.ToString(@"mm\:ss");
        BtnRec1.Content = rec && idx == 0 ? $"⏹  Dừng {elapsedText}" : "⏺  Quay 1";
        BtnRec2.Content = rec && idx == 1 ? $"⏹  Dừng {elapsedText}" : "⏺  Quay 2";
        BtnRec1.IsEnabled = !rec || idx == 0;
        BtnRec2.IsEnabled = !rec || idx == 1;
        if (rec)
            StatusText.Text = $"⏺ Đang quay màn hình {idx + 1} — {elapsedText}";
    }

    /// <summary>Put a send-flow milestone on the status bar and into crash.log.</summary>
    private void SetStage(string text)
    {
        StatusText.Text = text;
        CrashLog.WriteNote("Stage", text);
    }

    // ------------------------------------------------- in-page helper

    /// <summary>
    /// Script injected into every muse.ai document. It does ONE thing:
    /// find the chat composer (shadow-DOM aware) so the host can focus it
    /// right before pasting a capture. The v1.5.0 redesign removed the
    /// send-interception bridge entirely at the user's order — Enter is
    /// never held, nothing is auto-attached at send time; captures happen
    /// only from the three toolbar buttons / their hotkeys and are pasted
    /// into the composer immediately.
    /// </summary>
    private const string BridgeScript = """
        (function () {
          if (window.__museBridgeInstalled) return;
          window.__museBridgeInstalled = true;

          function deepQuery(root, sel, out) {
            var els = root.querySelectorAll(sel);
            for (var i = 0; i < els.length; i++) out.push(els[i]);
            var all = root.querySelectorAll('*');
            for (var j = 0; j < all.length; j++) {
              if (all[j].shadowRoot) deepQuery(all[j].shadowRoot, sel, out);
            }
          }

          function findComposer() {
            var found = [];
            deepQuery(document, '[contenteditable], [role="textbox"], textarea', found);
            var best = null, bestBottom = -1;
            for (var i = 0; i < found.length; i++) {
              var el = found[i];
              if (el.tagName === 'INPUT') continue;
              var r = el.getBoundingClientRect();
              if (r.width === 0 || r.height === 0) continue;
              if (r.bottom > bestBottom) { bestBottom = r.bottom; best = el; }
            }
            return best;
          }

          

          window.__museFocusComposer = function () {
            var c = findComposer();
            if (!c) return false;
            c.focus();
            return true;
          };

          

          function __museKillSpell(el) {
            try {
              if (el && (el.isContentEditable || el.tagName === 'TEXTAREA' || el.tagName === 'INPUT')) {
                // Guarded writes: re-setting the same value would feed
                // the MutationObserver below an endless record stream.
                if (el.spellcheck !== false) el.spellcheck = false;
                if (el.getAttribute('spellcheck') !== 'false') el.setAttribute('spellcheck', 'false');
              }
            } catch (e) {}
          }
          function __museKillSpellAll() {
            try {
              var list = document.querySelectorAll('[contenteditable], textarea, input');
              for (var i = 0; i < list.length; i++) __museKillSpell(list[i]);
              if (document.activeElement) __museKillSpell(document.activeElement);
            } catch (e) {}
          }
          __museKillSpellAll();
          // This script runs at document creation, when
          // document.documentElement may not exist yet — observe the
          // Document node itself (always present) and re-run once the
          // DOM is parsed. (First lab run caught the element-targeted
          // observer dying silently on exactly this.)
          try {
            new MutationObserver(function () { __museKillSpellAll(); }).observe(document, {
              childList: true, subtree: true,
              attributes: true, attributeFilter: ['contenteditable', 'spellcheck'],
            });
          } catch (e) {}
          document.addEventListener('DOMContentLoaded', __museKillSpellAll);

          window.addEventListener('contextmenu', function (ev) {
            ev.preventDefault();
            ev.stopPropagation();
            try {
              window.chrome.webview.postMessage(JSON.stringify({ type: 'muse-contextmenu' }));
            } catch (e) {}
          }, true);

          window.__museDiag = function () {
            var c = findComposer();
            return { bridge: true, composer: !!c, url: location.href };
          };

          try {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'muse-bridge-ready' }));
          } catch (e) {}
        })();
        """;

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string type = "";
        try
        {
            // The page posts a JSON *string*. WebMessageAsJson hands that back
            // wrapped in yet another layer of JSON quotes, so field lookups on
            // it throw — and v1.2.0 then silently dropped every single page
            // message (bridge-ready and send-request alike). Unwrap the plain
            // string first, then parse it.
            string raw = e.TryGetWebMessageAsString() ?? e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t))
            {
                type = t.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("WebMessageParse", ex);
            return;
        }

        if (type == "muse-bridge-ready")
            CrashLog.WriteNote("WebMessage", "bridge-ready");
        else if (type == "muse-contextmenu")
            Dispatcher.BeginInvoke(new Action(ShowAppContextMenu));
    }

    private async Task<string> EvalAsync(string js)
    {
        try { return (await Web.CoreWebView2.ExecuteScriptAsync(js)) ?? ""; }
        catch { return ""; }
    }

    private async Task<bool> EvalBoolAsync(string js)
    {
        return (await EvalAsync(js)).Trim() == "true";
    }

}
