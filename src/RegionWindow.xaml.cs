using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DRectangle = System.Drawing.Rectangle;

namespace MuseDesktop;

/// <summary>
/// Full-screen drag-to-select overlay — the WinLens "lens" interaction
/// (adapted from WinLens, MIT, Copyright (c) 2026 Marco Beltrame —
/// RegionSelectionWindow). The window is pinned in PHYSICAL pixels, the
/// desktop around the selection is dimmed by four rectangles so the
/// selection itself stays crystal clear, a chip shows the size in real
/// pixels while dragging, and the result is returned in virtual-screen
/// physical pixels via <see cref="SelectedRect"/> (null on cancel).
/// </summary>
public partial class RegionWindow : Window
{
    private const int MinSizePx = 10;

    private readonly DRectangle _virtualBounds;
    private double _pixelToDip;

    private Point? _dragStart;

    /// <summary>Selected region in physical screen pixels, or null if cancelled.</summary>
    public DRectangle? SelectedRect { get; private set; }

    public RegionWindow()
    {
        InitializeComponent();

        _virtualBounds = Capture.VirtualScreenRect();

        // Pinned in PHYSICAL pixels so it really covers every screen,
        // also when it starts on a monitor scaled differently from the
        // primary. The px→DIP factor is re-read from the window's own
        // DPI in Loaded / OnDpiChanged — otherwise the rectangle the
        // user draws maps back to the wrong physical pixels and the
        // capture is offset (the old single-scale-factor version had
        // exactly this failure mode on mixed-DPI desks).
        _pixelToDip = WindowPlacement.SystemPixelToDip();
        WindowPlacement.PinToPhysicalBounds(this, _virtualBounds);

        Loaded += (_, _) =>
        {
            _pixelToDip = WindowPlacement.PixelToDip(this, _pixelToDip);
            LayoutDimFullCover();
            Activate();
            Focus();
        };

        // The window can be resized after Loaded (re-pinned after a DPI
        // change); keep the dim layer covering all of it while idle.
        SizeChanged += (_, _) =>
        {
            if (IsLoaded && _dragStart is null) LayoutDimFullCover();
        };

        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += (_, _) => Cancel();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (newDpi.DpiScaleX > 0) _pixelToDip = 1.0 / newDpi.DpiScaleX;

        // A drag in progress was measured in the old DIPs; drop it
        // rather than capture the wrong pixels. The user drags again.
        if (_dragStart is not null)
        {
            ReleaseMouseCapture();
            _dragStart = null;
            Sel.Visibility = Visibility.Collapsed;
            SizeChip.Visibility = Visibility.Collapsed;
        }
        if (IsLoaded) LayoutDimFullCover();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel();
    }

    private void Cancel()
    {
        SelectedRect = null;
        DialogResult = false;
        Close();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Root);
        Hint.Visibility = Visibility.Collapsed;
        Sel.Visibility = Visibility.Visible;
        SizeChip.Visibility = Visibility.Visible;
        Canvas.SetLeft(Sel, _dragStart.Value.X);
        Canvas.SetTop(Sel, _dragStart.Value.Y);
        Sel.Width = 0;
        Sel.Height = 0;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start) return;
        var current = e.GetPosition(Root);

        double x = Math.Min(start.X, current.X);
        double y = Math.Min(start.Y, current.Y);
        double w = Math.Abs(current.X - start.X);
        double h = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(Sel, x);
        Canvas.SetTop(Sel, y);
        Sel.Width = w;
        Sel.Height = h;

        LayoutDim(x, y, w, h);

        // Show pixel dimensions of the prospective capture (physical px).
        int pxW = (int)Math.Round(w / _pixelToDip);
        int pxH = (int)Math.Round(h / _pixelToDip);
        SizeText.Text = $"{pxW} × {pxH}";
        SizeChip.UpdateLayout();
        double chipW = SizeChip.ActualWidth;
        double chipH = SizeChip.ActualHeight;

        // Place the size chip just below-right of the cursor, on screen.
        double chipX = Math.Min(ActualWidth - chipW - 4, current.X + 12);
        double chipY = Math.Min(ActualHeight - chipH - 4, current.Y + 12);
        chipX = Math.Max(4, chipX);
        chipY = Math.Max(4, chipY);
        Canvas.SetLeft(SizeChip, chipX);
        Canvas.SetTop(SizeChip, chipY);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        ReleaseMouseCapture();
        _dragStart = null;

        double dipX = Canvas.GetLeft(Sel);
        double dipY = Canvas.GetTop(Sel);
        double dipW = Sel.Width;
        double dipH = Sel.Height;

        int pxW = (int)Math.Round(dipW / _pixelToDip);
        int pxH = (int)Math.Round(dipH / _pixelToDip);

        if (pxW < MinSizePx || pxH < MinSizePx)
        {
            SelectedRect = null;
        }
        else
        {
            int pxX = _virtualBounds.X + (int)Math.Round(dipX / _pixelToDip);
            int pxY = _virtualBounds.Y + (int)Math.Round(dipY / _pixelToDip);
            SelectedRect = new DRectangle(pxX, pxY, pxW, pxH);
        }
        DialogResult = SelectedRect != null;
        Close();
    }

    // Sized from ActualWidth/ActualHeight rather than Width/Height: the
    // window is repositioned in physical pixels after construction, so
    // the DIP values set in the constructor are stale by the time these
    // run.
    private void LayoutDimFullCover()
    {
        Canvas.SetLeft(DimTop, 0); Canvas.SetTop(DimTop, 0);
        DimTop.Width = ActualWidth; DimTop.Height = ActualHeight;
        DimBottom.Width = 0; DimBottom.Height = 0;
        DimLeft.Width = 0; DimLeft.Height = 0;
        DimRight.Width = 0; DimRight.Height = 0;
    }

    private void LayoutDim(double x, double y, double w, double h)
    {
        double right = x + w;
        double bottom = y + h;

        Canvas.SetLeft(DimTop, 0); Canvas.SetTop(DimTop, 0);
        DimTop.Width = ActualWidth; DimTop.Height = Math.Max(0, y);

        Canvas.SetLeft(DimBottom, 0); Canvas.SetTop(DimBottom, bottom);
        DimBottom.Width = ActualWidth; DimBottom.Height = Math.Max(0, ActualHeight - bottom);

        Canvas.SetLeft(DimLeft, 0); Canvas.SetTop(DimLeft, y);
        DimLeft.Width = Math.Max(0, x); DimLeft.Height = h;

        Canvas.SetLeft(DimRight, right); Canvas.SetTop(DimRight, y);
        DimRight.Width = Math.Max(0, ActualWidth - right); DimRight.Height = h;
    }
}
