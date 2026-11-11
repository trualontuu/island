using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Island.Services;

public static class CaptureService
{
    /// <summary>Снимок прямоугольника экрана в физических пикселях (GDI BitBlt, без System.Drawing).</summary>
    public static BitmapSource Capture(int x, int y, int w, int h)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr bmp = Native.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = Native.SelectObject(mem, bmp);
        try
        {
            Native.BitBlt(mem, 0, 0, w, h, screen, x, y, Native.SRCCOPY | Native.CAPTUREBLT);
            Native.SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // приводим к Bgra32 c непрозрачной альфой
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            var wb = new WriteableBitmap(conv);
            wb.Freeze();
            return wb;
        }
        finally
        {
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static (Native.RECT bounds, double scale, int index) MonitorUnderCursor()
    {
        Native.GetCursorPos(out var p);
        var (b, _, s, i) = Native.MonitorOf(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST));
        return (b, s, i);
    }

    public static string SaveCopy(BitmapSource img, string folder, bool toClipboard = true)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        using (var fs = File.Create(path))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            enc.Save(fs);
        }
        if (toClipboard) ClipHelper.SetImage(img);
        return path;
    }

    // ---------------- OCR (встроенный в Windows) ----------------
    public static async Task<string> OcrAsync(BitmapSource img)
    {
        // мелкий текст распознаётся лучше в 2× масштабе
        BitmapSource src = img;
        if (img.PixelHeight < 400 && img.PixelWidth * 2 <= OcrEngine.MaxImageDimension)
            src = new TransformedBitmap(img, new ScaleTransform(2, 2));
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        using var sb = SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);

        // пробуем языки профиля (обычно ru + en), берём результат с бо́льшим количеством текста
        string best = "";
        foreach (var lang in OcrEngine.AvailableRecognizerLanguages.Take(3))
        {
            var eng = OcrEngine.TryCreateFromLanguage(lang);
            if (eng == null) continue;
            var r = await eng.RecognizeAsync(sb);
            var text = string.Join(Environment.NewLine, r.Lines.Select(l => l.Text));
            if (text.Count(char.IsLetterOrDigit) > best.Count(char.IsLetterOrDigit)) best = text;
        }
        return best.Trim();
    }

    // ---------------- QR ----------------
    public static string? DecodeQr(BitmapSource img)
    {
        var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = true, Options = { TryHarder = true, TryInverted = true } };
        var res = reader.Decode(new ZXing.RGBLuminanceSource(px, w, h, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32));
        return res?.Text;
    }
}

/// <summary>Буфер обмена с повтором (его часто кратко блокируют другие процессы) и пометкой «это мы».</summary>
public static class ClipHelper
{
    public static DateTime OwnWriteUntil;

    static void Retry(Action a)
    {
        for (int i = 0; i < 5; i++)
        {
            try { a(); return; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(30); }
        }
    }

    public static void SetText(string s) { OwnWriteUntil = DateTime.UtcNow.AddMilliseconds(400); Retry(() => Clipboard.SetDataObject(s, true)); }
    public static void SetImage(BitmapSource b) { OwnWriteUntil = DateTime.UtcNow.AddMilliseconds(400); Retry(() => Clipboard.SetImage(b)); }
    public static void SetFiles(string[] paths)
    {
        var col = new System.Collections.Specialized.StringCollection();
        col.AddRange(paths);
        OwnWriteUntil = DateTime.UtcNow.AddMilliseconds(400);
        Retry(() => Clipboard.SetFileDropList(col));
    }
}
