using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Island.Core;

namespace Island.UI;

/// <summary>
/// Цветовые темы острова. Цвета лежат в ресурсах приложения (DynamicResource), поэтому смена темы
/// мгновенно перекрашивает остров и микшер без перезапуска.
/// </summary>
public sealed record Theme(string Id, string Name, Color Bg, Color Card, Color CardHover, Color Tab, Color Track, Color Accent, Color Eq, Color Dim)
{
    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static readonly Theme[] All =
    {
        new("black",    "Чёрная — как на iPhone", C(0x000000), C(0x1C1C1F), C(0x2A2A2E), C(0x2C2C30), C(0x3A3A3E), C(0x0A84FF), C(0x5AC8FA), C(0x8E8E93)),
        new("graphite", "Графит",                 C(0x1B1C21), C(0x2A2B32), C(0x35363F), C(0x383A43), C(0x464852), C(0x8F9BFF), C(0xB4BCFF), C(0x9A9BA6)),
        new("midnight", "Полночь — тёмно-синяя",  C(0x070D1F), C(0x121C36), C(0x1B2747), C(0x1F2D52), C(0x2A3A63), C(0x4C8DFF), C(0x7FB2FF), C(0x8796BA)),
        new("violet",   "Фиолетовая",             C(0x110820), C(0x1F1334), C(0x2A1B45), C(0x311F53), C(0x3D2A64), C(0xA970FF), C(0xC99BFF), C(0x9D8EBA)),
        new("emerald",  "Изумрудная",             C(0x03120D), C(0x0D241D), C(0x143128), C(0x173A2F), C(0x21483B), C(0x30D158), C(0x63E6BE), C(0x84A99B)),
        new("ocean",    "Океан — бирюзовая",      C(0x02161B), C(0x0A2830), C(0x11353E), C(0x133E48), C(0x1C4C57), C(0x22C3E6), C(0x6FE3F5), C(0x86A9B1)),
        new("sunset",   "Закат — тёплая",         C(0x1A0906), C(0x2E140E), C(0x3D1C14), C(0x452016), C(0x552A1E), C(0xFF7A45), C(0xFFB86B), C(0xB9978B)),
        new("rose",     "Розовая",                C(0x1A0812), C(0x2E1221), C(0x3D192D), C(0x461C34), C(0x562642), C(0xFF4F8B), C(0xFF8DB5), C(0xB98FA3)),
    };

    public static Theme Get(string? id) => Array.Find(All, t => t.Id == id) ?? All[0];

    /// <summary>Кадров в секунду для анимаций: из настроек или частота монитора.</summary>
    public static int Fps { get; private set; } = 60;

    /// <summary>Характер анимаций: множитель длительности, жёсткость пружины, отскок.</summary>
    public static double AnimMul { get; private set; } = 1;
    public static double StiffMul { get; private set; } = 1;
    public static bool Bounce { get; private set; } = true;
    public static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms * AnimMul);
    public static IEasingFunction Spring(double damping, double stiffness) => new SpringEase(Bounce ? damping : 1.0, stiffness * StiffMul);

    /// <summary>Применить тему, прозрачность и частоту кадров ко всему приложению.</summary>
    public static void Apply(Settings s)
    {
        var t = Get(s.Theme);
        double op = Math.Clamp(s.Opacity, 0.3, 1.0);
        var r = Application.Current.Resources;
        r["IslandBg"] = B(t.Bg, op);
        // плитки чуть плотнее фона, чтобы при прозрачности текст на них читался
        r["Card"] = B(t.Card, Math.Min(1, op + 0.1));
        r["CardHover"] = B(t.CardHover, Math.Min(1, op + 0.15));
        r["Tab"] = B(t.Tab, Math.Min(1, op + 0.15));
        r["Track"] = B(t.Track, 1);
        r["Accent"] = B(t.Accent, 1);
        r["Eq"] = B(t.Eq, 1);
        r["Dim"] = B(t.Dim, 1);
        r["AccentSoft"] = B(Color.FromArgb(120, t.Accent.R, t.Accent.G, t.Accent.B), 1);
        Fps = s.Fps > 0 ? s.Fps : MonitorHz();
        // 0 — очень плавно (длиннее, мягче), 50 — по умолчанию, 100 — резко (коротко, упруго)
        double k = Math.Clamp(s.AnimSpeed, 0, 100) / 100.0;
        AnimMul = k < 0.5 ? 1.6 - 1.2 * k : 1.0 - 1.1 * (k - 0.5);
        StiffMul = k < 0.5 ? 0.85 + 0.3 * k : 1.0 + 0.5 * (k - 0.5);
        Bounce = s.AnimBounce;
    }

    static SolidColorBrush B(Color c, double a)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * a), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    // ---------- частота монитора ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? dev, int mode, ref DEVMODE dm);

    public static int MonitorHz()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency >= 30) return Math.Min(dm.dmDisplayFrequency, 360);
        }
        catch { }
        return 60;
    }
}

/// <summary>
/// Пружина (как в iOS): быстрый старт без рывка и мягкая остановка с лёгким «перелётом».
/// damping &lt; 1 — небольшой отскок, 1 — без отскока.
/// </summary>
public sealed class SpringEase : IEasingFunction
{
    readonly double _z, _w;
    public SpringEase(double damping = 0.8, double stiffness = 7.5)
    {
        _z = damping;
        _w = stiffness / damping; // огибающая e^(-z·w·t) к концу анимации почти ноль
    }

    public double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double w = _w, z = _z;
        if (z >= 1) return 1 - (1 + w * t) * Math.Exp(-w * t);
        double wd = w * Math.Sqrt(1 - z * z);
        return 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + z * w / wd * Math.Sin(wd * t));
    }
}
