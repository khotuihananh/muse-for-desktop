using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace MuseDesktop;

public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    static App()
    {
        // Force per-monitor v2 DPI awareness before any HWND is created
        // (the WinLens lens does the same). Without it, GetSystemMetrics
        // and CopyFromScreen can return DPI-scaled logical pixels and
        // captures/selections land offset on mixed-DPI multi-monitor desks.
        try { SetProcessDpiAwarenessContext(PerMonitorAwareV2); } catch { /* older OS */ }
    }

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private Forms.NotifyIcon? _tray;

    internal MainWindow? MainWin { get; private set; }
    internal bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Never die silently: log every unhandled error and keep the app alive
        // where the UI thread can safely continue.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("Dispatcher", args.Exception);
            args.Handled = true;
            MessageBox.Show(
                "Muse Desktop vừa gặp lỗi nhưng đã được giữ lại, không tự tắt nữa.\n\n" +
                args.Exception.Message +
                "\n\nChi tiết lỗi đã ghi vào file crash.log trong thư mục:\n" + CrashLog.FolderPath,
                "Muse Desktop",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("Task", args.Exception);
            args.SetObserved();
        };

        _mutex = new Mutex(initiallyOwned: true, "MuseDesktop.SingleInstance.v1", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already resident: ask it to show itself, then quit.
            try { EventWaitHandle.OpenExisting("MuseDesktop.ShowEvent.v1").Set(); }
            catch { /* Nothing sensible to do; just exit. */ }
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "MuseDesktop.ShowEvent.v1");
        Task.Run(() =>
        {
            while (true)
            {
                _showSignal.WaitOne();
                Dispatcher.BeginInvoke(new Action(() => MainWin?.ToggleShow(forceShow: true)));
            }
        });

        MainWin = new MainWindow();
        // Create the native handle now so the global hotkeys work before the first show.
        new WindowInteropHelper(MainWin).EnsureHandle();

        SetupTray();
        MainWin.Show();
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Text = "Muse Desktop",
            Visible = true,
            Icon = LoadTrayIcon(),
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mở Muse", null, (_, _) => Ui(() => MainWin?.ToggleShow(forceShow: true)));
        menu.Items.Add("Len: khoanh vùng → chụp → dán vào chat", null,
            (_, _) => Ui(() => { if (MainWin != null) _ = MainWin.CaptureRegionAndPaste(); }));
        menu.Items.Add("Chụp màn hình 1 → dán vào chat", null,
            (_, _) => Ui(() => { if (MainWin != null) _ = MainWin.CaptureScreenAndPaste(0); }));
        menu.Items.Add("Chụp màn hình 2 → dán vào chat", null,
            (_, _) => Ui(() => { if (MainWin != null) _ = MainWin.CaptureScreenAndPaste(1); }));
        menu.Items.Add("Quay / dừng màn hình 1 → dán video vào chat", null,
            (_, _) => Ui(() => { if (MainWin != null) _ = MainWin.ToggleRecord(0); }));
        menu.Items.Add("Quay / dừng màn hình 2 → dán video vào chat", null,
            (_, _) => Ui(() => { if (MainWin != null) _ = MainWin.ToggleRecord(1); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Gán phím chụp…", null, (_, _) => Ui(() => MainWin?.OpenHotkeyDialog()));
        menu.Items.Add("Ẩn / hiện thanh công cụ", null, (_, _) => Ui(() => MainWin?.ToggleChrome()));
        menu.Items.Add("Tải lại trang Muse", null, (_, _) => Ui(() => MainWin?.ReloadPage()));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var autoItem = new Forms.ToolStripMenuItem("Khởi động cùng Windows")
        {
            Checked = Autostart.IsEnabled(),
            CheckOnClick = true,
        };
        autoItem.CheckedChanged += (_, _) => Autostart.Set(autoItem.Checked);
        menu.Items.Add(autoItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Thoát", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Ui(() => MainWin?.ToggleShow(forceShow: true));
    }

    private void Ui(Action action) => Dispatcher.BeginInvoke(action);

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null) return icon;
            }
        }
        catch { /* Fall through to the system icon. */ }
        return System.Drawing.SystemIcons.Application;
    }

    private void ExitApp()
    {
        IsExiting = true;
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        try { MainWin?.Close(); }
        catch (InvalidOperationException) { /* Window already closing. */ }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
