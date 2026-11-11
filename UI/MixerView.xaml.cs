using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Island.Services;

namespace Island.UI;

/// <summary>
/// Микшер: общая громкость + каждое приложение со звуком, живые индикаторы уровня.
/// Индикаторы обновляются на каждом кадре (до 240 к/с) ТОЛЬКО пока микшер на экране;
/// меняется лишь ScaleX трансформации — без пересчёта раскладки.
/// </summary>
public partial class MixerView : UserControl
{
    sealed class Row
    {
        public required AppAudio App;
        public required Slider Slider;
        public required TextBlock Pct;
        public required Button Mute;
        public required ScaleTransform Meter;
        public float Shown;
    }

    AudioMixerService? _svc;
    readonly List<Row> _rows = new();
    bool _live, _updating;
    float _masterShown;
    TimeSpan _lastFrame;
    public int Fps { get; set; } = Theme.Fps;
    public bool Compact { get; set; }

    public MixerView() => InitializeComponent();

    public void Attach(AudioMixerService svc)
    {
        _svc = svc;
        svc.AppsChanged += () => { if (_live) BuildRows(); };
        svc.MasterChanged += () => { if (_live) SyncMaster(); };
        svc.AppVolumeChanged += () => { if (_live) SyncApps(); };
        svc.DeviceChanged += () => { if (_live) { DeviceName.Text = svc.DeviceName; if (DevicePanel.Visibility == Visibility.Visible) BuildDevices(); } };
    }

    /// <summary>Показали микшер — включаем живые индикаторы.</summary>
    public void Start()
    {
        if (_svc == null || _live) return;
        _live = true;
        DeviceName.Text = _svc.DeviceName;
        DevicePanel.Visibility = Visibility.Collapsed;
        _svc.Refresh(); // вызовет BuildRows
        SyncMaster();
        // «двигатель» кадров: пустая анимация с нужной частотой поднимает частоту рендера WPF
        var a = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(a, Fps);
        Ticker.BeginAnimation(OpacityProperty, a);
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Спрятали — всё останавливаем, ноль работы.</summary>
    public void Stop()
    {
        if (!_live) return;
        _live = false;
        CompositionTarget.Rendering -= OnFrame;
        Ticker.BeginAnimation(OpacityProperty, null);
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (_svc == null) return;
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastFrame) return; // WPF иногда шлёт событие дважды за кадр
        double dt = _lastFrame == TimeSpan.Zero ? 0.004 : Math.Min(0.1, (t - _lastFrame).TotalSeconds);
        _lastFrame = t;
        float decay = (float)(dt * 1.8); // плавное падение, мгновенный рост

        float mp = _svc.SamplePeaks();
        _masterShown = mp > _masterShown ? mp : Math.Max(mp, _masterShown - decay);
        MasterMeterScale.ScaleX = _masterShown;

        foreach (var r in _rows)
        {
            float p = r.App.ShownPeak;
            float s = p > r.Shown ? p : Math.Max(p, r.Shown - decay);
            if (Math.Abs(s - r.Shown) > 0.002f || (s == 0 && r.Shown != 0)) { r.Shown = s; r.Meter.ScaleX = s; }
        }
    }

    // ---------------- общая ----------------
    void SyncMaster()
    {
        if (_svc == null) return;
        _updating = true;
        MasterSlider.Value = _svc.MasterVolume;
        _updating = false;
        MasterPct.Text = Math.Round(_svc.MasterVolume * 100).ToString();
        MasterMute.Content = VolGlyph(_svc.MasterVolume, _svc.MasterMute);
        MasterMute.Foreground = _svc.MasterMute ? new SolidColorBrush(Color.FromRgb(255, 69, 58)) : Brushes.White;
    }

    void MasterSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        MasterPct.Text = Math.Round(e.NewValue * 100).ToString();
        if (_updating || _svc == null) return;
        _svc.MasterVolume = (float)e.NewValue;
        MasterMute.Content = VolGlyph((float)e.NewValue, false);
    }

    void MasterMute_Click(object s, RoutedEventArgs e) { if (_svc != null) { _svc.MasterMute = !_svc.MasterMute; SyncMaster(); } }

    static string VolGlyph(float v, bool mute) => mute || v < 0.01 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

    // ---------------- приложения ----------------
    void BuildRows()
    {
        if (_svc == null) return;
        _rows.Clear();
        AppList.Children.Clear();
        if (_svc.Apps.Count == 0)
            AppList.Children.Add(new TextBlock { Text = "Сейчас ни одно приложение не издаёт звук", Foreground = (Brush)FindResource("Dim"), FontSize = 12, Margin = new Thickness(6, 4, 6, 8) });
        foreach (var app in _svc.Apps) AppList.Children.Add(MakeRow(app));
    }

    FrameworkElement MakeRow(AppAudio app)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        // иконка приложения
        FrameworkElement icon;
        var src = app.ExePath != null ? IslandWindow.FileIcon(app.ExePath) : null;
        if (src != null) icon = new Image { Source = src, Width = 26, Height = 26 };
        else icon = new TextBlock { Text = app.IsSystem ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var iconBox = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), Child = icon, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(iconBox);

        var mid = new StackPanel { Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = app.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 0, 1), Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)) });
        var slider = new Slider { Style = (Style)FindResource("Vol"), Value = app.Volume };
        mid.Children.Add(slider);
        var meterScale = new ScaleTransform(0, 1);
        mid.Children.Add(new Border
        {
            Height = 2.5, CornerRadius = new CornerRadius(1.25), Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), Margin = new Thickness(2, 2, 2, 0), ClipToBounds = true,
            Child = new Rectangle { Fill = (Brush)FindResource("Meter"), RenderTransform = meterScale, RenderTransformOrigin = new Point(0, 0.5) },
        });
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        var pct = new TextBlock { Text = Math.Round(app.Volume * 100).ToString(), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 2, 0) };
        Grid.SetColumn(pct, 2);
        g.Children.Add(pct);

        var mute = new Button { Style = (Style)FindResource("Ico"), Content = VolGlyph(app.Volume, app.Mute), Margin = new Thickness(0, 12, 0, 0) };
        if (app.Mute) mute.Foreground = new SolidColorBrush(Color.FromRgb(255, 69, 58));
        Grid.SetColumn(mute, 3);
        g.Children.Add(mute);

        var row = new Row { App = app, Slider = slider, Pct = pct, Mute = mute, Meter = meterScale };
        slider.ValueChanged += (_, e) =>
        {
            pct.Text = Math.Round(e.NewValue * 100).ToString();
            if (_updating) return;
            app.Volume = (float)e.NewValue;
            if (app.Mute && e.NewValue > 0) app.Mute = false;
            mute.Content = VolGlyph((float)e.NewValue, false);
            mute.Foreground = Brushes.White;
        };
        mute.Click += (_, _) =>
        {
            app.Mute = !app.Mute;
            mute.Content = VolGlyph(app.Volume, app.Mute);
            mute.Foreground = app.Mute ? new SolidColorBrush(Color.FromRgb(255, 69, 58)) : Brushes.White;
        };
        _rows.Add(row);
        return new Border { Padding = new Thickness(6, 6, 4, 6), CornerRadius = new CornerRadius(12), Child = g, Background = Brushes.Transparent };
    }

    void SyncApps()
    {
        _updating = true;
        foreach (var r in _rows)
        {
            float v = r.App.Volume;
            if (Math.Abs(r.Slider.Value - v) > 0.005) r.Slider.Value = v;
            r.Pct.Text = Math.Round(v * 100).ToString();
            r.Mute.Content = VolGlyph(v, r.App.Mute);
            r.Mute.Foreground = r.App.Mute ? new SolidColorBrush(Color.FromRgb(255, 69, 58)) : Brushes.White;
        }
        _updating = false;
    }

    // ---------------- устройства ----------------
    void DeviceBtn_Click(object s, RoutedEventArgs e)
    {
        bool show = DevicePanel.Visibility != Visibility.Visible;
        DevicePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DeviceChevron.Text = show ? "" : "";
        if (show) BuildDevices();
    }

    void BuildDevices()
    {
        if (_svc == null) return;
        DeviceList.Children.Clear();
        foreach (var d in _svc.Devices())
        {
            var sp = new DockPanel();
            var check = new TextBlock { Text = d.IsDefault ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(10, 132, 255)), Width = 20, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(check, Dock.Right);
            sp.Children.Add(check);
            sp.Children.Add(new TextBlock { Text = d.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = d.IsDefault ? FontWeights.SemiBold : FontWeights.Normal });
            var b = new Button { Style = (Style)FindResource("DevRow"), Content = sp };
            var id = d.Id;
            b.Click += (_, _) =>
            {
                _svc.SetDefault(id);
                DevicePanel.Visibility = Visibility.Collapsed;
                DeviceChevron.Text = "";
            };
            DeviceList.Children.Add(b);
        }
    }
}
