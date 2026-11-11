using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Island.Core;
using Island.Services;
using Microsoft.Win32;

namespace Island.UI;

/// <summary>Настройки — обычное окно, создаётся только по запросу и освобождается после закрытия.</summary>
public sealed class SettingsWindow : Window
{
    readonly Settings _s;
    readonly IslandWindow _island;
    readonly StackPanel _p = new() { Margin = new Thickness(22, 18, 22, 18) };
    static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(22, 22, 24));
    static readonly Brush Field = new SolidColorBrush(Color.FromRgb(36, 36, 40));
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(142, 142, 147));

    readonly PasswordBox _key = new();
    readonly TextBox _model = Tb(), _hkShelf = Tb(), _hkChat = Tb(), _hkShot = Tb(), _ffmpeg = Tb(), _fps = Tb(), _folder = Tb(), _pinned = Tb(true), _sys = Tb(true);
    readonly ComboBox _effort = new();
    readonly CheckBox _mic = Cb("Записывать микрофон вместе со звуком системы"), _hideFs = Cb("Прятать остров в полноэкранных играх и видео"),
        _eq = Cb("Анимированный эквалайзер при воспроизведении"), _mascot = Cb("Показывать маскота (глазки)"), _auto = Cb("Запускать вместе с Windows"),
        _volIcon = Cb("Клик по значку громкости Windows открывает микшер острова (нужен перезапуск)");
    readonly TextBlock _claudeState = new() { Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    readonly TextBlock _ocState = new() { Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

    public SettingsWindow(Settings s, IslandWindow island)
    {
        _s = s; _island = island;
        Title = "Island — настройки";
        Width = 560; Height = 760; MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg; Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;

        Header("Нейросеть");
        Row("API-ключ Anthropic", _key, "Пусто — берётся из переменной ANTHROPIC_API_KEY");
        Row("Модель", _model);
        foreach (var e in new[] { "low", "medium", "high", "max" }) _effort.Items.Add(e);
        // системный шаблон ComboBox рисует светлый фон и игнорирует наш тёмный — значит, текст в нём тёмный
        var item = new Style(typeof(ComboBoxItem));
        item.Setters.Add(new Setter(ForegroundProperty, Brushes.Black));
        item.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 5, 8, 5)));
        _effort.ItemContainerStyle = item;
        Row("Глубина размышлений (effort)", _effort, "low — быстрые ответы, high/max — сложные задачи");
        Row("Системная подсказка", _sys);

        Header("Горячие клавиши");
        Row("Полка и буфер", _hkShelf, "Например: Alt+Space, Ctrl+Shift+V, Win+F1");
        Row("Чат с нейросетью", _hkChat);
        Row("Скриншот области", _hkShot);

        Header("Скриншоты и запись");
        Row("Папка", _folder);
        Row("ffmpeg", _ffmpeg, "Путь к ffmpeg.exe или просто ffmpeg, если он в PATH");
        Row("Кадров в секунду", _fps);
        _p.Children.Add(_mic);

        Header("Поведение");
        _p.Children.Add(_hideFs); _p.Children.Add(_eq); _p.Children.Add(_mascot); _p.Children.Add(_auto); _p.Children.Add(_volIcon);
        Row("Закреплённые приложения (по одному пути в строке)", _pinned);

        Header("Агенты");
        var claude = Btn("Подключить Claude Code", () =>
        {
            try { var f = AgentIntegration.InstallClaude(_s.AgentPort); MessageBox.Show(this, "Хуки добавлены в\n" + f + "\n\nРезервная копия: settings.json.island-backup.\nПерезапусти сессии Claude Code.", "Island"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Island"); }
            RefreshAgentState();
        });
        var claudeOff = Btn("Отключить", () => { AgentIntegration.UninstallClaude(); RefreshAgentState(); });
        _p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), Children = { claude, claudeOff, _claudeState } });
        var oc = Btn("Подключить opencode", () =>
        {
            try { var f = AgentIntegration.InstallOpencode(_s.AgentPort); MessageBox.Show(this, "Плагин создан:\n" + f + "\n\nПерезапусти opencode.", "Island"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Island"); }
            RefreshAgentState();
        });
        var ocOff = Btn("Отключить", () => { AgentIntegration.UninstallOpencode(); RefreshAgentState(); });
        _p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), Children = { oc, ocOff, _ocState } });

        var save = Btn("Сохранить", Save);
        save.Background = new SolidColorBrush(Color.FromRgb(10, 132, 255));
        save.Margin = new Thickness(0, 22, 0, 0);
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Padding = new Thickness(26, 8, 26, 8);
        _p.Children.Add(save);

        Content = new ScrollViewer { Content = _p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Load();
    }

    void Load()
    {
        _key.Password = _s.ApiKey;
        _model.Text = _s.Model;
        _effort.SelectedItem = _effort.Items.Cast<string>().FirstOrDefault(x => x == _s.Effort) ?? "low";
        _sys.Text = _s.SystemPrompt;
        _hkShelf.Text = _s.HotkeyShelf; _hkChat.Text = _s.HotkeyChat; _hkShot.Text = _s.HotkeyScreenshot;
        _folder.Text = _s.CaptureFolder; _ffmpeg.Text = _s.FfmpegPath; _fps.Text = _s.RecordFps.ToString();
        _mic.IsChecked = _s.RecordMic; _hideFs.IsChecked = _s.HideInFullscreen; _eq.IsChecked = _s.AnimateEqualizer;
        _mascot.IsChecked = _s.Mascot; _auto.IsChecked = _s.Autostart; _volIcon.IsChecked = _s.VolumeIconMixer;
        _pinned.Text = string.Join(Environment.NewLine, _s.PinnedApps);
        RefreshAgentState();
    }

    void RefreshAgentState()
    {
        _claudeState.Text = AgentIntegration.ClaudeInstalled() ? "✓ подключено" : "не подключено";
        _ocState.Text = AgentIntegration.OpencodeInstalled() ? "✓ подключено" : "не подключено";
    }

    void Save()
    {
        _s.ApiKey = _key.Password.Trim();
        _s.Model = string.IsNullOrWhiteSpace(_model.Text) ? "claude-opus-5-5" : _model.Text.Trim();
        _s.Effort = _effort.SelectedItem as string ?? "low";
        _s.SystemPrompt = _sys.Text;
        _s.HotkeyShelf = _hkShelf.Text.Trim(); _s.HotkeyChat = _hkChat.Text.Trim(); _s.HotkeyScreenshot = _hkShot.Text.Trim();
        _s.CaptureFolder = _folder.Text.Trim(); _s.FfmpegPath = string.IsNullOrWhiteSpace(_ffmpeg.Text) ? "ffmpeg" : _ffmpeg.Text.Trim();
        if (int.TryParse(_fps.Text, out int fps)) _s.RecordFps = Math.Clamp(fps, 10, 120);
        _s.RecordMic = _mic.IsChecked == true; _s.HideInFullscreen = _hideFs.IsChecked == true;
        _s.AnimateEqualizer = _eq.IsChecked == true; _s.Mascot = _mascot.IsChecked == true; _s.Autostart = _auto.IsChecked == true; _s.VolumeIconMixer = _volIcon.IsChecked == true;
        _s.PinnedApps = _pinned.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _s.Save();
        SetAutostart(_s.Autostart);
        _island.ApplySettings();
        Close();
    }

    public static void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return;
            if (on) k.SetValue("Island", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Island", false);
        }
        catch { }
    }

    // ---------------- элементы ----------------
    void Header(string t) => _p.Children.Add(new TextBlock { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8) });

    void Row(string label, Control c, string? hint = null)
    {
        _p.Children.Add(new TextBlock { Text = label, Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 6, 0, 3) });
        StyleField(c);
        _p.Children.Add(c);
        if (hint != null) _p.Children.Add(new TextBlock { Text = hint, Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), Opacity = 0.75 });
    }

    static void StyleField(Control c)
    {
        c.Background = Field; c.Foreground = Brushes.White; c.BorderThickness = new Thickness(0); c.Padding = new Thickness(8, 6, 8, 6);
        if (c is TextBox tb) tb.CaretBrush = Brushes.White;
        if (c is PasswordBox pb) pb.CaretBrush = Brushes.White;
        if (c is ComboBox) c.Foreground = Brushes.Black;
    }

    static TextBox Tb(bool multi = false) => new() { AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multi ? 70 : 0, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    static CheckBox Cb(string t) => new() { Content = t, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };

    static Button Btn(string text, Action a)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), Background = Field, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        b.Click += (_, _) => a();
        return b;
    }
}
