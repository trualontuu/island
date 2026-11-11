using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Island.Services;

namespace Island.UI;

/// <summary>Вкладка «Система»: процессор, память, видеокарта, диск, сеть — графики за минуту.</summary>
public partial class IslandWindow
{
    SystemMonitor? _sys;
    readonly DispatcherTimer _sysTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    bool _sysBuilt;
    long _sysLast;
    (TextBlock Val, TextBlock Sub, Spark G) _cCpu, _cRam, _cGpu, _cDisk, _cNet;
    TextBlock? _uptime;

    static readonly Color CCpu = Color.FromRgb(10, 132, 255), CRam = Color.FromRgb(191, 90, 242), CGpu = Color.FromRgb(48, 209, 88),
        CDisk = Color.FromRgb(255, 159, 10), CDown = Color.FromRgb(90, 200, 250), CUp = Color.FromRgb(255, 100, 130);

    void BuildSystem()
    {
        if (_sysBuilt) return;
        _sysBuilt = true;
        _cCpu = Card("", "Процессор", CCpu, new Thickness(0, 0, 6, 6));
        _cRam = Card("", "Память", CRam, new Thickness(0, 0, 6, 6));
        _cGpu = Card("", "Видеокарта", CGpu, new Thickness(0, 0, 0, 6));
        _cDisk = Card("", "Диск", CDisk, new Thickness(0, 0, 6, 0));
        _cNet = Card("", "Сеть", CDown, new Thickness(0, 0, 6, 0));
        _cNet.G.Max = 0; _cNet.G.Color2 = CUp;

        // последняя плитка: время работы + диспетчер задач
        _uptime = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        var tm = new Button { Style = (Style)FindResource("Tile"), Height = 36, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
        tm.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = {
            new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = "  Диспетчер задач", FontSize = 12 } } };
        tm.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { } Collapse(); };
        var last = new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 10, 14, 12),
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                new TextBlock { Text = "Компьютер работает", FontSize = 12, Foreground = (Brush)FindResource("Dim") }, _uptime, tm } },
        };
        last.SetResourceReference(Border.BackgroundProperty, "Card");
        PageSystem.Children.Add(last);
    }

    (TextBlock, TextBlock, Spark) Card(string glyph, string name, Color c, Thickness margin)
    {
        var val = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Text = "—", HorizontalAlignment = HorizontalAlignment.Right };
        var head = new DockPanel();
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = {
            new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, Foreground = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = "  " + name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center } } });
        var sub = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 6) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var g = new Spark { Color = c };
        var dp = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(sub, Dock.Top);
        dp.Children.Add(head); dp.Children.Add(sub); dp.Children.Add(g);
        var b = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 10, 14, 10), Margin = margin, Child = dp, ClipToBounds = true };
        b.SetResourceReference(Border.BackgroundProperty, "Card");
        PageSystem.Children.Add(b);
        return (val, sub, g);
    }

    void StartSystem()
    {
        BuildSystem();
        if (_sys == null)
        {
            _sys = new SystemMonitor();
            _sys.BeginInit();
            _sysTimer.Tick += (_, _) => { _sys.Sample(); _sysLast = Environment.TickCount64; RefreshSystem(); };
        }
        if (_sysTimer.IsEnabled) return;
        // долго не смотрели — старая история только сбивает с толку
        if (_sysLast != 0 && Environment.TickCount64 - _sysLast > 5000) { _sys.Cpu.Clear(); _sys.Ram.Clear(); _sys.Gpu.Clear(); _sys.Disk.Clear(); _sys.NetDown.Clear(); _sys.NetUp.Clear(); }
        _sys.Sample(); _sysLast = Environment.TickCount64;
        RefreshSystem();
        _sysTimer.Start();
    }

    void StopSystem() => _sysTimer.Stop();

    void RefreshSystem()
    {
        var m = _sys;
        if (m == null) return;
        Set(_cCpu, $"{m.CpuNow:0}%", $"{Environment.ProcessorCount} потоков", m.Cpu);
        Set(_cRam, $"{m.RamNow:0}%", $"{m.RamUsedGb:0.0} из {m.RamTotalGb:0.#} ГБ", m.Ram);
        Set(_cGpu, m.GpuNow < 0 ? "—" : $"{m.GpuNow:0}%", m.GpuNow < 0 ? "нет данных" : "самый загруженный движок", m.Gpu);
        Set(_cDisk, m.DiskNow < 0 ? "—" : $"{m.DiskNow:0}%", "активность всех дисков", m.Disk);
        _cNet.Val.Text = Speed(m.DownNow);
        _cNet.Sub.Text = $"↓ {Speed(m.DownNow)}   ↑ {Speed(m.UpNow)}";
        _cNet.G.Values = m.NetDown; _cNet.G.Values2 = m.NetUp; _cNet.G.Update();
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (_uptime != null) _uptime.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays} д {up.Hours} ч" : up.Hours > 0 ? $"{up.Hours} ч {up.Minutes} мин" : $"{up.Minutes} мин";
    }

    static void Set((TextBlock Val, TextBlock Sub, Spark G) c, string val, string sub, System.Collections.Generic.IReadOnlyCollection<double> hist)
    {
        c.Val.Text = val; c.Sub.Text = sub;
        c.G.Values = hist; c.G.Update();
    }

    static string Speed(double bps) => bps >= 1048576 ? $"{bps / 1048576:0.0} МБ/с" : bps >= 1024 ? $"{bps / 1024:0} КБ/с" : $"{bps:0} Б/с";
}
