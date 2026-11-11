using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Island.Core;
using Island.Services;

namespace Island.UI;

/// <summary>
/// Микшер над треем (открывается кликом по значку громкости Windows).
/// Обычное (не layered) окно: фон-акрил и скругление рисует DWM на видеокарте,
/// поэтому анимации и индикаторы на 240 к/с почти не стоят CPU. Окно создаётся один раз и прячется.
/// </summary>
public sealed class MixerFlyout : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }

    readonly MixerView _view = new() { Fps = Theme.Fps };
    readonly Border _card;
    readonly TranslateTransform _slide = new();
    DateTime _hiddenAt;

    public MixerFlyout(AudioMixerService svc)
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Width = 400; SizeToContent = SizeToContent.Height; MaxHeight = 620;
        Background = Brushes.Transparent; ShowActivated = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0) });

        _view.Attach(svc);
        _card = new Border
        {
            // полупрозрачный тон поверх акрила; без акрила (старые сборки) — просто тёмный фон
            Background = new SolidColorBrush(Color.FromArgb(150, 18, 18, 20)),
            Padding = new Thickness(16, 14, 16, 14),
            Child = _view,
            RenderTransform = _slide,
        };
        Content = _card;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(h)!.CompositionTarget!.BackgroundColor = Colors.Transparent;
            int dark = 1, round = 2, backdrop = 3; // тёмная тема, скругление, акрил
            DwmSetWindowAttribute(h, 20, ref dark, 4);
            DwmSetWindowAttribute(h, 33, ref round, 4);
            DwmSetWindowAttribute(h, 38, ref backdrop, 4);
            var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
            DwmExtendFrameIntoClientArea(h, ref m);
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        };
        Deactivated += (_, _) => Close2();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close2(); };
    }

    /// <summary>Открыть/закрыть (повторный клик по значку закрывает).</summary>
    public void Toggle()
    {
        if (IsVisible) { Close2(); return; }
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 350) return; // клик по значку уже закрыл окно через Deactivated
        Open();
    }

    void Open()
    {
        _view.Start();
        Show();
        UpdateLayout();
        Place();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);

        // въезд снизу: 240 к/с, пружинка
        var a = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
        var f = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        Timeline.SetDesiredFrameRate(a, 240); Timeline.SetDesiredFrameRate(f, 240);
        _slide.BeginAnimation(TranslateTransform.YProperty, a);
        _card.BeginAnimation(OpacityProperty, f);
    }

    public void Close2()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        _view.Stop();
        Hide();
    }

    /// <summary>Над треем, в правом нижнем углу рабочей области монитора с панелью задач.</summary>
    void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        var mon = tray != IntPtr.Zero ? Native.MonitorFromWindow(tray, Native.MONITOR_DEFAULTTONEAREST) : Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTOPRIMARY);
        var (_, work, scale, _) = Native.MonitorOf(mon);
        int w = (int)Math.Ceiling(ActualWidth * scale), hh = (int)Math.Ceiling(ActualHeight * scale);
        int margin = (int)(12 * scale);
        Native.SetWindowPos(h, Native.HWND_TOPMOST, work.Right - w - margin, work.Bottom - hh - margin, w, hh, Native.SWP_NOACTIVATE);
    }
}
