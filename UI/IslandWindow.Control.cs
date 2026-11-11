using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Island.Services;

namespace Island.UI;

public partial class IslandWindow
{
    DateTime _restartArmed, _shutdownArmed;
    bool _happBusy;

    async Task RefreshControlAsync()
    {
        if (HappIcon.Source == null && HappService.Installed) HappIcon.Source = FileIcon(HappService.Exe);
        if (!_happBusy) ShowHapp(await Task.Run(HappService.State));
        await FillVpnAsync();
    }

    void ShowHapp(HappState s)
    {
        HappCard.Visibility = s == HappState.NotInstalled ? Visibility.Collapsed : Visibility.Visible;
        bool on = s is HappState.Tun or HappState.Proxy;
        HappToggle.IsChecked = on;
        HappStatus.Text = s switch
        {
            HappState.Tun => "Подключено · режим TUN (весь трафик)",
            HappState.Proxy => "Подключено · режим прокси",
            _ => "Отключено",
        };
        var srv = HappService.CurrentServer();
        if (srv != null) HappStatus.Text += " · " + srv;
        HappDot.Fill = on ? new SolidColorBrush(Color.FromRgb(52, 199, 89)) : (Brush)FindResource("Dim");
    }

    async void HappToggle_Click(object s, RoutedEventArgs e)
    {
        if (_happBusy) return;
        _happBusy = true;
        bool on = HappToggle.IsChecked == true;
        HappStatus.Text = on ? "Подключаю…" : "Отключаю…";
        HappDot.Fill = new SolidColorBrush(Color.FromRgb(255, 214, 10));
        var st = await HappService.ToggleAsync(on);
        _happBusy = false;
        ShowHapp(st);
        bool ok = on == st is HappState.Tun or HappState.Proxy;
        Toast(ok ? (on ? "Happ подключён" : "Happ отключён") : "Happ не ответил — открой его", "", ok ? Mood.Happy : Mood.Alert);
    }

    void HappOpen_Click(object s, RoutedEventArgs e) { Collapse(); HappService.OpenApp(); }

    async Task FillVpnAsync()
    {
        var list = await ControlService.VpnListAsync();
        VpnList.Children.Clear();
        if (list.Count == 0) VpnList.Children.Add(Note("Встроенных VPN-подключений нет"));
        foreach (var v in list)
        {
            var row = RowButton("", v.Name, v.Connected ? "Подключено" : null, v.Connected);
            var vpn = v;
            row.Click += async (_, _) =>
            {
                SetRowSub(row, vpn.Connected ? "Отключаю…" : "Подключаю…");
                var err = await ControlService.ToggleVpnAsync(vpn);
                if (err != null) Toast(err, "", Mood.Alert, 4);
                else Toast(vpn.Connected ? "VPN отключён" : "VPN подключён", "", Mood.Happy);
                await FillVpnAsync();
            };
            VpnList.Children.Add(row);
        }
    }

    void VpnSettings_Click(object s, RoutedEventArgs e) => ControlService.OpenUri("ms-settings:network-vpn");

    // ---------------- питание ----------------
    void Power_Lock(object s, RoutedEventArgs e) { Collapse(); ControlService.Lock(); }
    void Power_Sleep(object s, RoutedEventArgs e) { Collapse(); ControlService.Sleep(); }

    void Power_Restart(object s, RoutedEventArgs e)
    {
        if ((DateTime.Now - _restartArmed).TotalSeconds < 3) { ControlService.Restart(); return; }
        _restartArmed = DateTime.Now;
        RestartText.Text = "  Ещё раз — перезагрузка";
        After(TimeSpan.FromSeconds(3), () => RestartText.Text = "  Перезагрузка");
    }

    void Power_Shutdown(object s, RoutedEventArgs e)
    {
        if ((DateTime.Now - _shutdownArmed).TotalSeconds < 3) { ControlService.Shutdown(); return; }
        _shutdownArmed = DateTime.Now;
        ShutdownText.Text = "  Ещё раз — выключить";
        After(TimeSpan.FromSeconds(3), () => ShutdownText.Text = "  Выключение");
    }

    void After(TimeSpan delay, Action a)
    {
        var t = new DispatcherTimer { Interval = delay };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    // ---------------- элементы списков ----------------
    TextBlock Note(string t) => new() { Text = t, Foreground = (Brush)FindResource("Dim"), FontSize = 12, Margin = new Thickness(6, 4, 6, 4), TextWrapping = TextWrapping.Wrap };

    Button RowButton(string glyph, string title, string? sub, bool accent = false)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ic = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 20, Foreground = accent ? new SolidColorBrush(Color.FromRgb(52, 199, 89)) : Brushes.White };
        var tt = new TextBlock { Text = title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        var st = new TextBlock { Text = sub ?? "", FontSize = 11, Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Tag = "sub" };
        Grid.SetColumn(tt, 1); Grid.SetColumn(st, 2);
        g.Children.Add(ic); g.Children.Add(tt); g.Children.Add(st);
        return new Button { Style = (Style)FindResource("Row"), Content = g };
    }

    static void SetRowSub(Button row, string text)
    {
        if (row.Content is Grid g)
            foreach (var c in g.Children)
                if (c is TextBlock { Tag: "sub" } tb) tb.Text = text;
    }
}
