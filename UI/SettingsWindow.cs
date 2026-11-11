using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Island.Core;
using Island.Services;
using Microsoft.Win32;

namespace Island.UI;

/// <summary>
/// Настройки: боковое меню с разделами, тёмные поля и переключатели, живой предпросмотр темы,
/// прозрачности и анимаций. Создаётся только по запросу и освобождается после закрытия.
/// </summary>
public sealed class SettingsWindow : Window
{
    readonly Settings _s;
    readonly IslandWindow _island;
    readonly ContentControl _page = new();
    readonly Dictionary<string, FrameworkElement> _pages = new();
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

    // нейросеть
    readonly PasswordBox _key = new();
    readonly TextBox _model = new(), _baseUrl = new(), _sys = Multi();
    readonly ComboBox _provider = new();
    readonly TextBlock _keyHint = Hint("");
    Button _getKey = null!;
    readonly Dictionary<string, AiProvider> _ai = new();
    string _cur = "";
    static readonly (string Id, string Title)[] Providers =
    {
        ("openrouter", "OpenRouter — бесплатные модели (Gemma, Nemotron и др.)"),
        ("deepseek", "DeepSeek API — платно, но дёшево"),
        ("custom", "Свой OpenAI-совместимый сервер"),
    };

    // клавиши, запись
    readonly TextBox _hkShelf = new(), _hkChat = new(), _hkShot = new(), _hkSystem = new(), _ffmpeg = new(), _recFps = new(), _folder = new(), _pinned = Multi();

    // оформление
    readonly List<RadioButton> _themeCards = new();
    readonly Slider _opacity = new() { Minimum = 30, Maximum = 100, IsSnapToTickEnabled = true, TickFrequency = 5 };
    readonly Slider _anim = new() { Minimum = 0, Maximum = 100, IsSnapToTickEnabled = true, TickFrequency = 5 };
    readonly TextBlock _opacityVal = Val(), _animVal = Val();
    readonly ComboBox _animFps = new();
    static readonly int[] FpsChoices = { 0, 60, 75, 120, 144, 165, 240 };

    // переключатели
    CheckBox _mic = null!, _hideFs = null!, _eq = null!, _mascot = null!, _auto = null!, _volIcon = null!, _sleep = null!, _autoExp = null!, _bounce = null!;
    readonly TextBlock _claudeState = Hint(""), _ocState = Hint("");

    bool _loaded, _saved;

    public SettingsWindow(Settings s, IslandWindow island)
    {
        _s = s; _island = island;
        Title = "Island — настройки";
        Width = 880; Height = 640; MinWidth = 720; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/SettingsStyles.xaml") });
        SetResourceReference(BackgroundProperty, "WinBg");
        SourceInitialized += (_, _) => DarkTitleBar();

        BuildPages();

        // ---- каркас: меню слева, раздел справа, кнопки снизу ----
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var side = new Border { Padding = new Thickness(14, 18, 14, 14) };
        side.SetResourceReference(Border.BackgroundProperty, "SideBg");
        Grid.SetRowSpan(side, 2);
        var nav = new DockPanel();
        side.Child = nav;
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 22) };
        logo.Children.Add(new Border
        {
            Width = 34, Height = 16, CornerRadius = new CornerRadius(8), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), BorderThickness = new Thickness(1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { Eye(), Eye() } },
        });
        logo.Children.Add(new StackPanel { Margin = new Thickness(10, 0, 0, 0), Children = {
            new TextBlock { Text = "Island", FontSize = 16, FontWeight = FontWeights.SemiBold },
            Hint("настройки") } });
        DockPanel.SetDock(logo, Dock.Top);
        nav.Children.Add(logo);

        var folder = new Button { Style = (Style)FindResource("Btn"), Content = "Папка настроек", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 7), FontSize = 12 };
        folder.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", Settings.Dir); } catch { } };
        DockPanel.SetDock(folder, Dock.Bottom);
        nav.Children.Add(folder);

        var items = new StackPanel();
        nav.Children.Add(items);
        RadioButton? first = null;
        foreach (var (id, glyph, title) in new[]
        {
            ("look", "", "Оформление"), ("behavior", "", "Поведение"), ("ai", "", "Нейросеть"),
            ("keys", "", "Горячие клавиши"), ("rec", "", "Скриншоты и запись"), ("agents", "", "Агенты"),
        })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Nav"), Tag = id };
            rb.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Width = 26, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center } } };
            rb.Checked += (_, _) => { _page.Content = _pages[id]; _scroll.ScrollToTop(); };
            items.Children.Add(rb);
            first ??= rb;
        }
        root.Children.Add(side);

        _scroll.Content = new Border { Padding = new Thickness(32, 26, 32, 24), Child = _page };
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(_scroll);

        var bar = new Border { Padding = new Thickness(32, 12, 32, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        bar.SetResourceReference(Border.BorderBrushProperty, "SLine");
        Grid.SetColumn(bar, 1); Grid.SetRow(bar, 1);
        var cancel = new Button { Style = (Style)FindResource("Btn"), Content = "Отмена", Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => Close();
        var save = new Button { Style = (Style)FindResource("Primary"), Content = "Сохранить", IsDefault = true };
        save.Click += (_, _) => Save();
        bar.Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } };
        root.Children.Add(bar);

        Content = root;
        Load();
        first!.IsChecked = true;
        _loaded = true;
        // закрыли без «Сохранить» — возвращаем прежние тему, прозрачность и анимации
        Closed += (_, _) => { if (!_saved) _island.ApplySettings(); };
    }

    // =====================================================================
    //  Разделы
    // =====================================================================
    void BuildPages()
    {
        // ---------- Оформление ----------
        var look = Page("Оформление", "Цвет, прозрачность и анимации. Остров меняется сразу — посмотри на него.");
        var themes = Card(look, "Цветовая тема");
        var wrap = new WrapPanel();
        foreach (var t in Theme.All) wrap.Children.Add(ThemeCard(t));
        themes.Children.Add(wrap);

        var bg = Card(look, "Фон");
        _opacity.ValueChanged += (_, _) => { _opacityVal.Text = $"{_opacity.Value:0}%"; Preview(); };
        bg.Children.Add(SliderRow("Непрозрачность", _opacity, _opacityVal, "прозрачный", "сплошной"));
        bg.Children.Add(Hint("Меньше 100% — сквозь остров видно рабочий стол. Плитки внутри остаются чуть плотнее, чтобы текст читался."));

        var an = Card(look, "Анимации");
        _anim.ValueChanged += (_, _) => { _animVal.Text = AnimName(_anim.Value); Preview(); };
        an.Children.Add(SliderRow("Характер", _anim, _animVal, "плавно", "резко"));
        _bounce = Switch(an, "Пружинный отскок", "Лёгкий «перелёт» при раскрытии, как у iPhone. Выключи — движение без отскока.");
        _bounce.Click += (_, _) => Preview();
        Line(an);
        foreach (var f in FpsChoices) _animFps.Items.Add(new ComboBoxItem { Tag = f, Content = f == 0 ? $"Как у монитора ({Theme.MonitorHz()} Гц)" : $"{f} кадров/с" });
        _animFps.SelectionChanged += (_, _) => Preview();
        Field(an, "Кадров в секунду", _animFps, "Больше — плавнее, меньше — легче для слабых ПК");
        var test = new Button { Style = (Style)FindResource("Btn"), Content = "▶  Показать анимацию на острове", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 4) };
        test.Click += (_, _) => _island.PreviewAnimation();
        an.Children.Add(test);

        var face = Card(look, "Капсула");
        _mascot = Switch(face, "Маскот (глазки)", "Моргают, радуются и засыпают в капсуле");
        Line(face);
        _eq = Switch(face, "Эквалайзер при музыке", "Анимированные полоски справа в капсуле");
        _pages["look"] = look;

        // ---------- Поведение ----------
        var beh = Page("Поведение", "Как остров появляется, прячется и раскрывается.");
        var c1 = Card(beh, null);
        _autoExp = Switch(c1, "Раскрывать при наведении", "Подвёл курсор к острову — он раскрылся без клика. Увёл — свернулся.");
        Line(c1);
        _sleep = Switch(c1, "Спящий режим", "Мышь не двигается 20 секунд — остров уезжает за край экрана и возвращается при движении");
        Line(c1);
        _hideFs = Switch(c1, "Прятать в полноэкранных играх и видео", null);
        Line(c1);
        _volIcon = Switch(c1, "Микшер по клику на значок громкости", "Клик по значку громкости Windows открывает микшер острова (нужен перезапуск)");
        Line(c1);
        _auto = Switch(c1, "Запускать вместе с Windows", null);
        var c2 = Card(beh, "Закреплённые приложения");
        c2.Children.Add(_pinned);
        c2.Children.Add(Hint("По одному пути к .exe в строке — появятся иконками на главной вкладке"));
        _pages["behavior"] = beh;

        // ---------- Нейросеть ----------
        var ai = Page("Нейросеть", "Чат внутри острова: Ctrl+Alt+Space (можно поменять в «Горячих клавишах»).");
        var a1 = Card(ai, null);
        foreach (var (id, title) in Providers) _provider.Items.Add(new ComboBoxItem { Content = title, Tag = id });
        _provider.SelectionChanged += (_, _) => { StoreCurrent(); _cur = (_provider.SelectedItem as ComboBoxItem)?.Tag as string ?? "openrouter"; ShowProvider(); };
        Field(a1, "Кто отвечает", _provider, null);
        Field(a1, "API-ключ", _key, null);
        a1.Children.Add(_keyHint);
        _getKey = new Button { Style = (Style)FindResource("Btn"), Content = "Получить ключ", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        _getKey.Click += (_, _) => { var u = AiProvider.KeyUrl(_cur); if (u.Length > 0) ControlService.OpenUri(u); };
        a1.Children.Add(_getKey);
        var a2 = Card(ai, "Дополнительно");
        Field(a2, "Адрес API", _baseUrl, null);
        Field(a2, "Модель", _model, "OpenRouter: бесплатные модели оканчиваются на «:free»; openrouter/free — любая доступная бесплатная");
        Field(a2, "Системная подсказка", _sys, "Как нейросеть должна себя вести");
        _pages["ai"] = ai;

        // ---------- Клавиши ----------
        var keys = Page("Горячие клавиши", "Например: Alt+Space, Ctrl+Shift+V, Win+F1. Пусто — без клавиши.");
        var k1 = Card(keys, null);
        Field(k1, "Полка и история буфера", _hkShelf, null);
        Field(k1, "Чат с нейросетью", _hkChat, null);
        Field(k1, "Скриншот области", _hkShot, null);
        Field(k1, "Нагрузка на ПК (вкладка «Система»)", _hkSystem, null);
        _pages["keys"] = keys;

        // ---------- Запись ----------
        var rec = Page("Скриншоты и запись", "Снимки и видео сохраняются в папку ниже.");
        var r1 = Card(rec, null);
        Field(r1, "Папка", _folder, null);
        Field(r1, "ffmpeg", _ffmpeg, "Путь к ffmpeg.exe или просто ffmpeg, если он в PATH");
        Field(r1, "Кадров в секунду записи", _recFps, "10–120");
        Line(r1);
        _mic = Switch(r1, "Записывать микрофон", "Вместе со звуком системы");
        _pages["rec"] = rec;

        // ---------- Агенты ----------
        var ag = Page("Агенты", "Остров показывает, что делают Claude Code и opencode, и даёт ответить на их вопросы.");
        var g1 = Card(ag, null);
        g1.Children.Add(AgentRow("Claude Code", _claudeState,
            () => { var f = AgentIntegration.InstallClaude(_s.AgentPort); return "Хуки добавлены в\n" + f + "\n\nРезервная копия: settings.json.island-backup.\nПерезапусти сессии Claude Code."; },
            AgentIntegration.UninstallClaude));
        Line(g1);
        g1.Children.Add(AgentRow("opencode", _ocState,
            () => { var f = AgentIntegration.InstallOpencode(_s.AgentPort); return "Плагин создан:\n" + f + "\n\nПерезапусти opencode."; },
            AgentIntegration.UninstallOpencode));
        _pages["agents"] = ag;
    }

    FrameworkElement AgentRow(string name, TextBlock state, Func<string> install, Action uninstall)
    {
        var on = new Button { Style = (Style)FindResource("Btn"), Content = "Подключить", Margin = new Thickness(0, 0, 8, 0) };
        on.Click += (_, _) =>
        {
            try { MessageBox.Show(this, install(), "Island"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Island"); }
            RefreshAgentState();
        };
        var off = new Button { Style = (Style)FindResource("Btn"), Content = "Отключить" };
        off.Click += (_, _) => { uninstall(); RefreshAgentState(); };
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Children = { on, off } };
        DockPanel.SetDock(btns, Dock.Right);
        row.Children.Add(btns);
        row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold }, state } });
        return row;
    }

    RadioButton ThemeCard(Theme t)
    {
        // мини-остров в цветах темы: капсула с глазками, плитка и эквалайзер
        var eq = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 10) };
        foreach (var h in new[] { 6.0, 12, 8, 14 }) eq.Children.Add(new Border { Width = 3, Height = h, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(t.Eq), Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Bottom });
        var preview = new Border
        {
            Width = 128, Height = 76, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(t.Bg),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), BorderThickness = new Thickness(1),
            Child = new Grid { Children = {
                new Border { Width = 44, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0),
                    Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { Eye(), Eye() } } },
                new Border { Width = 50, Height = 34, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(t.Card), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 9),
                    Child = new Border { Width = 26, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(t.Accent), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(7, 0, 0, 7) } },
                eq } },
        };
        var rb = new RadioButton { Style = (Style)FindResource("ThemeCard"), Tag = t.Id, Margin = new Thickness(0, 0, 10, 10) };
        rb.Content = new StackPanel { Children = { preview, new TextBlock { Text = t.Name.Split(" — ")[0], FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2) } } };
        rb.ToolTip = t.Name;
        rb.Checked += (_, _) => Preview();
        _themeCards.Add(rb);
        return rb;
    }

    static FrameworkElement Eye() => new Border { Width = 3.2, Height = 4.6, CornerRadius = new CornerRadius(1.6), Background = Brushes.White, Margin = new Thickness(1.2, 0, 1.2, 0) };

    static string AnimName(double v) => v switch { < 15 => "очень плавно", < 40 => "плавно", < 61 => "обычно", < 86 => "быстро", _ => "резко" };

    // =====================================================================
    //  Загрузка / сохранение / предпросмотр
    // =====================================================================
    void Load()
    {
        foreach (var id in new[] { "openrouter", "deepseek", "custom" })
        {
            var a = _s.AiFor(id);
            _ai[id] = new AiProvider { Key = a.Key, BaseUrl = a.BaseUrl, Model = a.Model };
        }
        _cur = "";
        foreach (ComboBoxItem it in _provider.Items) if ((string)it.Tag == _s.ChatProvider) _provider.SelectedItem = it;
        if (_provider.SelectedItem == null) _provider.SelectedIndex = 0;
        _sys.Text = _s.SystemPrompt;
        _hkShelf.Text = _s.HotkeyShelf; _hkChat.Text = _s.HotkeyChat; _hkShot.Text = _s.HotkeyScreenshot; _hkSystem.Text = _s.HotkeySystem;
        _folder.Text = _s.CaptureFolder; _ffmpeg.Text = _s.FfmpegPath; _recFps.Text = _s.RecordFps.ToString();
        _mic.IsChecked = _s.RecordMic; _hideFs.IsChecked = _s.HideInFullscreen; _eq.IsChecked = _s.AnimateEqualizer;
        _mascot.IsChecked = _s.Mascot; _auto.IsChecked = _s.Autostart; _volIcon.IsChecked = _s.VolumeIconMixer; _sleep.IsChecked = _s.AutoSleep;
        _autoExp.IsChecked = _s.AutoExpand; _bounce.IsChecked = _s.AnimBounce;
        var th = Theme.Get(_s.Theme).Id;
        foreach (var rb in _themeCards) rb.IsChecked = (string)rb.Tag == th;
        _opacity.Value = Math.Round(Math.Clamp(_s.Opacity, 0.3, 1) * 100);
        _opacityVal.Text = $"{_opacity.Value:0}%";
        _anim.Value = Math.Clamp(_s.AnimSpeed, 0, 100);
        _animVal.Text = AnimName(_anim.Value);
        foreach (ComboBoxItem it in _animFps.Items) if ((int)it.Tag == _s.Fps) _animFps.SelectedItem = it;
        if (_animFps.SelectedItem == null) _animFps.SelectedIndex = 0;
        _pinned.Text = string.Join(Environment.NewLine, _s.PinnedApps);
        RefreshAgentState();
    }

    void StoreCurrent()
    {
        if (_cur.Length == 0) return;
        _ai[_cur] = new AiProvider { Key = _key.Password, BaseUrl = _baseUrl.Text, Model = _model.Text };
    }

    void ShowProvider()
    {
        var a = _ai.TryGetValue(_cur, out var x) ? x : AiProvider.Preset(_cur);
        _key.Password = a.Key; _baseUrl.Text = a.BaseUrl; _model.Text = a.Model;
        _getKey.Visibility = AiProvider.KeyUrl(_cur).Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _keyHint.Text = _cur switch
        {
            "openrouter" => "Бесплатно: openrouter.ai → Keys → Create Key. Деньги не нужны, есть лимит запросов. Если модель пропадёт — остров сам переключится на любую бесплатную.",
            "deepseek" => "platform.deepseek.com → пополнить баланс → API Keys. Очень дёшево, но не бесплатно.",
            _ => "Любой сервис с OpenAI-совместимым API (/chat/completions): Groq, Gemini, локальная LM Studio/Ollama и т.п.",
        };
    }

    void RefreshAgentState()
    {
        _claudeState.Text = AgentIntegration.ClaudeInstalled() ? "✓ подключено" : "не подключено";
        _ocState.Text = AgentIntegration.OpencodeInstalled() ? "✓ подключено" : "не подключено";
    }

    string SelectedTheme() => _themeCards.FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? _s.Theme;
    int SelectedFps() => (_animFps.SelectedItem as ComboBoxItem)?.Tag is int f ? f : 0;

    /// <summary>Предпросмотр на живом острове (без сохранения).</summary>
    void Preview()
    {
        if (!_loaded) return;
        Theme.Apply(new Settings
        {
            Theme = SelectedTheme(),
            Opacity = _opacity.Value / 100.0,
            Fps = SelectedFps(),
            AnimSpeed = (int)_anim.Value,
            AnimBounce = _bounce.IsChecked == true,
        });
    }

    void Save()
    {
        StoreCurrent();
        _s.Provider = _cur.Length > 0 ? _cur : "openrouter";
        foreach (var (id, a) in _ai) _s.Ai[id] = new AiProvider { Key = a.Key.Trim(), BaseUrl = a.BaseUrl.Trim(), Model = a.Model.Trim() };
        _s.SystemPrompt = _sys.Text;
        _s.HotkeyShelf = _hkShelf.Text.Trim(); _s.HotkeyChat = _hkChat.Text.Trim(); _s.HotkeyScreenshot = _hkShot.Text.Trim(); _s.HotkeySystem = _hkSystem.Text.Trim();
        _s.CaptureFolder = _folder.Text.Trim(); _s.FfmpegPath = string.IsNullOrWhiteSpace(_ffmpeg.Text) ? "ffmpeg" : _ffmpeg.Text.Trim();
        if (int.TryParse(_recFps.Text, out int fps)) _s.RecordFps = Math.Clamp(fps, 10, 120);
        _s.RecordMic = _mic.IsChecked == true; _s.HideInFullscreen = _hideFs.IsChecked == true;
        _s.AnimateEqualizer = _eq.IsChecked == true; _s.Mascot = _mascot.IsChecked == true; _s.Autostart = _auto.IsChecked == true;
        _s.VolumeIconMixer = _volIcon.IsChecked == true; _s.AutoSleep = _sleep.IsChecked == true; _s.AutoExpand = _autoExp.IsChecked == true;
        _s.Theme = SelectedTheme(); _s.Opacity = _opacity.Value / 100.0; _s.Fps = SelectedFps();
        _s.AnimSpeed = (int)_anim.Value; _s.AnimBounce = _bounce.IsChecked == true;
        _s.PinnedApps = _pinned.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _s.Save();
        _saved = true;
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

    /// <summary>Для тестового режима (--snap): показать раздел и отрисовать окно в PNG.</summary>
    internal void SnapPages(string dir)
    {
        foreach (var id in _pages.Keys)
        {
            _page.Content = null;
            // раздел целиком (без прокрутки) на фоне окна
            var pg = _pages[id];
            var host = new Border { Padding = new Thickness(32, 26, 32, 24), Child = pg, Width = 700 };
            host.SetResourceReference(Border.BackgroundProperty, "WinBg");
            host.Resources.MergedDictionaries.Add(Resources.MergedDictionaries[0]);
            AddLogicalChild(host);
            host.Measure(new Size(700, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            host.Child = null;
            RemoveLogicalChild(host);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "settings_" + id + ".png"));
            enc.Save(fs);
        }
    }

    // =====================================================================
    //  Элементы
    // =====================================================================
    static StackPanel Page(string title, string subtitle)
    {
        var p = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        var sub = Hint(subtitle);
        sub.FontSize = 12.5; sub.Margin = new Thickness(0, 4, 0, 18);
        p.Children.Add(sub);
        return p;
    }

    StackPanel Card(StackPanel page, string? header)
    {
        var inner = new StackPanel();
        if (header != null) inner.Children.Add(new TextBlock { Text = header, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 10) });
        var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 10, 18, 14), Margin = new Thickness(0, 0, 0, 14), Child = inner };
        b.SetResourceReference(Border.BackgroundProperty, "SCard");
        page.Children.Add(b);
        return inner;
    }

    static void Field(Panel card, string label, FrameworkElement c, string? hint)
    {
        var l = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 10, 0, 5) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "SDim");
        card.Children.Add(l);
        card.Children.Add(c);
        if (hint != null) { var h = Hint(hint); h.Margin = new Thickness(0, 5, 0, 0); card.Children.Add(h); }
    }

    CheckBox Switch(Panel card, string title, string? hint)
    {
        var content = new StackPanel { Children = { new TextBlock { Text = title, FontSize = 13.5 } } };
        if (hint != null) { var h = Hint(hint); h.Margin = new Thickness(0, 2, 0, 0); content.Children.Add(h); }
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), Content = content };
        card.Children.Add(cb);
        return cb;
    }

    static void Line(Panel card)
    {
        var l = new Border { Height = 1 };
        l.SetResourceReference(Border.BackgroundProperty, "SLine");
        card.Children.Add(l);
    }

    static FrameworkElement SliderRow(string label, Slider s, TextBlock val, string left, string right)
    {
        var head = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(new TextBlock { Text = label, FontSize = 13.5 });
        var ends = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var r = Hint(right); DockPanel.SetDock(r, Dock.Right);
        ends.Children.Add(r); ends.Children.Add(Hint(left));
        return new StackPanel { Children = { head, s, ends } };
    }

    static TextBlock Hint(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "SDim");
        return tb;
    }

    static TextBlock Val()
    {
        var tb = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        return tb;
    }

    static TextBox Multi() => new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    void DarkTitleBar()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4); // тёмный заголовок окна
        }
        catch { }
    }
}
