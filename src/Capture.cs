using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MuseDesktop;

internal static class Capture
{
    // Keep pasted screenshots comfortably below the chat composer's image limits
    // while staying readable: cap the width, keep the aspect ratio.
    private const int MaxWidth = 3200;

    public static Rectangle VirtualScreenRect()
    {
        int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        return new Rectangle(x, y, w, h);
    }

    public static Bitmap? FullScreen()
    {
        return CaptureRect(VirtualScreenRect());
    }

    public static Bitmap? CaptureRect(Rectangle rect, int maxWidth = MaxWidth)
    {
        // A maximized window's rectangle sticks out a few pixels past the screen
        // edges, and CopyFromScreen throws instead of clipping when any part of
        // the source lies outside the virtual screen. Clamp first.
        rect = Rectangle.Intersect(VirtualScreenRect(), rect);
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        }
        return ScaleDown(bmp, maxWidth);
    }

    private static Bitmap ScaleDown(Bitmap src, int maxWidth)
    {
        if (src.Width <= maxWidth) return src;
        int newHeight = Math.Max(1, (int)Math.Round(src.Height * (double)maxWidth / src.Width));
        var dst = new Bitmap(maxWidth, newHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, maxWidth, newHeight);
        }
        src.Dispose();
        return dst;
    }

    public static BitmapSource? ToBitmapSource(Bitmap bmp)
    {
        var hBitmap = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            Native.DeleteObject(hBitmap);
        }
    }

    /// <summary>JPEG-encode for transfer into the page bridge (base64 text).</summary>
    public static string ToJpegBase64(Bitmap bmp, int quality = 82)
    {
        using var ms = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bmp.Save(ms, codec, encParams);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static bool CopyToClipboard(Bitmap bmp)
    {
        var source = ToBitmapSource(bmp);
        if (source == null) return false;
        try
        {
            Clipboard.SetImage(source);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
