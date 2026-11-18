using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Island.UI;

/// <summary>
/// Цветные эмодзи в обычном TextBlock. WPF не умеет цветные шрифты, а Emoji.Wpf разбирал весь Segoe UI Emoji
/// целиком (+130 МБ памяти). Здесь читаем из шрифта только таблицы цветов COLR (v0: глиф → слои) и CPAL (палитра)
/// — точечно, по смещениям — и рисуем слои обычными GlyphRun. Картинки эмодзи кэшируются (замороженные).
/// Составные эмодзи (ZWJ, оттенки кожи, флаги) рисуются по частям — для уведомлений этого хватает.
/// </summary>
public static class EmojiText
{
    static bool _loaded, _ok;
    static GlyphTypeface? _face;
    static (ushort Gid, ushort First, ushort Count)[] _base = Array.Empty<(ushort, ushort, ushort)>();
    static byte[] _layers = Array.Empty<byte>();   // записи слоёв COLR v0: gid(2) + palette(2)
    static Color[] _pal = Array.Empty<Color>();
    static readonly Dictionary<int, DrawingImage?> _cache = new();

    static bool Load()
    {
        if (_loaded) return _ok;
        _loaded = true;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
            if (!File.Exists(path)) return false;
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            var head = Read(f, 0, 12);
            int n = U16(head, 4);
            var dir = Read(f, 12, 16 * n);
            long colr = -1, cpal = -1;
            for (int i = 0; i < n; i++)
            {
                var tag = Encoding.ASCII.GetString(dir, i * 16, 4);
                if (tag == "COLR") colr = U32(dir, i * 16 + 8);
                else if (tag == "CPAL") cpal = U32(dir, i * 16 + 8);
            }
            if (colr < 0 || cpal < 0) return false;

            var ch = Read(f, colr, 14);
            int nBase = U16(ch, 2), nLayers = U16(ch, 12);
            long baseOff = U32(ch, 4), layerOff = U32(ch, 8);
            var b = Read(f, colr + baseOff, nBase * 6);
            _base = new (ushort, ushort, ushort)[nBase];
            for (int i = 0; i < nBase; i++) _base[i] = (U16(b, i * 6), U16(b, i * 6 + 2), U16(b, i * 6 + 4));
            _layers = Read(f, colr + layerOff, nLayers * 4);

            var ph = Read(f, cpal, 12);
            int nEntries = U16(ph, 2), nRecords = U16(ph, 6);
            long recOff = U32(ph, 8);
            var first = Read(f, cpal + 12, 2); // палитра 0 начинается с этого индекса
            int start = U16(first, 0);
            var cols = Read(f, cpal + recOff + start * 4, nEntries * 4);
            _pal = new Color[nEntries];
            for (int i = 0; i < nEntries; i++) _pal[i] = Color.FromArgb(cols[i * 4 + 3], cols[i * 4 + 2], cols[i * 4 + 1], cols[i * 4]); // BGRA
            _ = nRecords;

            _face = new GlyphTypeface(new Uri(path));
            return _ok = true;
        }
        catch { return false; }
    }

    static byte[] Read(FileStream f, long at, int len)
    {
        var buf = new byte[len];
        f.Position = at;
        int got = 0;
        while (got < len) { int r = f.Read(buf, got, len - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
        return buf;
    }
    static ushort U16(byte[] a, int i) => (ushort)(a[i] << 8 | a[i + 1]);
    static uint U32(byte[] a, int i) => (uint)(a[i] << 24 | a[i + 1] << 16 | a[i + 2] << 8 | a[i + 3]);

    /// <summary>Цветная картинка эмодзи (em = 1) или null, если у символа нет цветных слоёв.</summary>
    static DrawingImage? Image(int cp)
    {
        if (_cache.TryGetValue(cp, out var img)) return img;
        img = null;
        if (_face!.CharacterToGlyphMap.TryGetValue(cp, out var gid))
        {
            int lo = 0, hi = _base.Length - 1, at = -1;
            while (lo <= hi) { int m = (lo + hi) >> 1; if (_base[m].Gid == gid) { at = m; break; } if (_base[m].Gid < gid) lo = m + 1; else hi = m - 1; }
            if (at >= 0)
            {
                const double em = 32; // рисуем в 32 px, картинка потом масштабируется — векторная, без потерь
                double adv = _face.AdvanceWidths[gid] * em, h = _face.Height * em, baseline = _face.Baseline * em;
                var g = new DrawingGroup();
                g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, adv, h)))); // постоянные границы
                var (_, first, count) = _base[at];
                for (int i = 0; i < count; i++)
                {
                    int o = (first + i) * 4;
                    if (o + 4 > _layers.Length) break;
                    ushort lg = U16(_layers, o), pi = U16(_layers, o + 2);
                    var col = pi == 0xFFFF || pi >= _pal.Length ? Colors.White : _pal[pi];
                    var brush = new SolidColorBrush(col); brush.Freeze();
                    var run = new GlyphRun(_face, 0, false, em, 1.0f, new[] { lg }, new Point(0, baseline), new[] { _face.AdvanceWidths[lg] * em },
                                           null, null, null, null, null, null);
                    g.Children.Add(new GlyphRunDrawing(brush, run));
                }
                g.Freeze();
                img = new DrawingImage(g);
                img.Freeze();
            }
        }
        if (_cache.Count < 512) _cache[cp] = img;
        return img;
    }

    /// <summary>Есть ли в строке что-то похожее на эмодзи (дешёвая проверка — шрифт не трогаем).</summary>
    static bool MaybeEmoji(string s)
    {
        foreach (var c in s) if (char.IsSurrogate(c) || (c >= '←' && c <= '⯿') || c == '©' || c == '®') return true;
        return false;
    }

    /// <summary>Записать текст в TextBlock, заменяя эмодзи цветными картинками.</summary>
    public static void Set(TextBlock tb, string text)
    {
        tb.Inlines.Clear();
        if (!MaybeEmoji(text) || !Load()) { tb.Text = text; return; }
        double size = tb.FontSize * 1.18;
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { tb.Inlines.Add(new Run(plain.ToString())); plain.Clear(); } }
        for (int i = 0; i < text.Length;)
        {
            int cp = Cp(text, i, out int len);
            i += len;
            if (cp is 0xFE0F or 0x200D || cp is >= 0x1F3FB and <= 0x1F3FF) continue; // вариант/ZWJ/оттенок кожи — пропускаем
            var img = cp > 0xFF ? Image(cp) : null;
            if (img == null) { plain.Append(char.ConvertFromUtf32(cp)); continue; }
            Flush();
            var w = size * img.Width / img.Height;
            tb.Inlines.Add(new InlineUIContainer(new System.Windows.Controls.Image { Source = img, Width = w, Height = size, Margin = new Thickness(1, 0, 1, 0) })
            { BaselineAlignment = BaselineAlignment.Center });
        }
        Flush();
    }

    static int Cp(string s, int i, out int len)
    {
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { len = 2; return char.ConvertToUtf32(s[i], s[i + 1]); }
        len = 1; return char.IsSurrogate(s[i]) ? 0xFFFD : s[i];
    }
}
