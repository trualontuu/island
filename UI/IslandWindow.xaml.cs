using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Island.Core;
using Island.Services;

namespace Island.UI;

public partial class IslandWindow : Window
{
    readonly Settings _s;
    readonly SystemHost _host;
    readonly MediaService _media;
    readonly ChatService _chat;
    readonly Recorder _rec;
    readonly ControlService _ctl = new();
    readonly AudioMixerService _mixer;
    MixerFlyout? _flyout;
    VolumeIconHook? _volHook;
    AgentServer? _agent;
    FullscreenWatcher? _fs;
    IntPtr _hwnd;

    // состояние
    bool _expanded, _pinned, _hiddenByFullscreen, _overlayOpen, _userHidden;
    string _page = "home";
    string? _toastText, _toastGlyph; Mood _toastMood;
    Size _target = new(150, 32);

    readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    readonly DispatcherTimer _toastTimer = new();
    readonly DispatcherTimer _agentDoneTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    readonly DispatcherTimer _trimTimer = new(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(8) };

    static readonly Size SizeHome = new(690, 292), SizeChat = new(690, 460), SizeControl = new(690, 330), SizeShelf = new(690, 330), SizeAgent = new(690, 262), SizeSound = new(690, 470), SizeSystem = new(690, 330);
    /// <summary>Частота кадров анимаций: из настроек или частота монитора.</summary>
    static int Fps => Theme.Fps;
    const double Pad = 24; // запас окна вокруг острова (тень/пружина)

    public IslandWindow(Settings s, SystemHost host)
    {
        _s = s; _host = host;
        InitializeComponent();
        _media = new MediaService(Dispatcher);
        _chat = new ChatService(s);
        _rec = new Recorder(s);
        _mixer = new AudioMixerService(Dispatcher);
        PageSound.Attach(_mixer);
        _rec.Error += m => Toast(m, "", Mood.Alert, 4);

        SourceInitialized += OnSourceInitialized;
        // скруглённая обрезка карточки плеера: размытая обложка и затемнение не вылезают за углы
        PlayerCard.SizeChanged += (_, e) => PlayerCard.Clip = new RectangleGeometry(new Rect(e.NewSize), 18, 18);
        Loaded += OnLoaded;

        Shell.MouseEnter += (_, _) => { _collapseTimer.Stop(); if (!_expanded) Hover(true); };
        Shell.MouseLeave += (_, _) => { if (!_expanded) Hover(false); else if (!_pinned) _collapseTimer.Start(); };
        Shell.MouseLeftButtonUp += Shell_Click;
        Shell.MouseRightButtonUp += (_, e) => { if (!_expanded) { ShowMenu(); e.Handled = true; } };
        Deactivated += (_, _) => { if (_expanded && !_pinned && !Shell.IsMouseOver) Collapse(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _expanded) { Collapse(); e.Handled = true; } };

        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_expanded || _pinned || Shell.IsMouseOver || _overlayOpen) return;
            if (ChatInput.IsKeyboardFocused && IsActive) return; // печатаешь — не мешаем
            Collapse();
        };
        _tick.Tick += (_, _) => OnTick();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toastText = null; _toastClick = null; UpdateCompact(); };
        _agentDoneTimer.Tick += (_, _) => { _agentDoneTimer.Stop(); _agent?.Dismiss(); };
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); if (!_expanded) Native.TrimMemory(); };

        // перетаскивание файлов на остров
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { if (!_expanded || _page != "shelf") Expand("shelf"); ShelfDrop.BorderBrush = Brushes.White; } };
        DragLeave += (_, _) => ShelfDrop.BorderBrush = Brushes.Transparent;
        Drop += (_, e) =>
        {
            ShelfDrop.BorderBrush = Brushes.Transparent;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddToShelf(files);
        };

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => PlaceWindow(_target), DispatcherPriority.Background);
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // не показываем в Alt+Tab
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        var src = HwndSource.FromHwnd(_hwnd);
        src?.AddHook(WndProc);
        // Прозрачное (layered) окно при GPU-рендере всё равно копирует каждый кадр обратно в память.
        // Для острова программный рендер дешевле (замер: CPU 1.1% против 3.9%, память 53 против 76 МБ).
        // Только для этого окна: микшер у трея — обычное окно и рисуется видеокартой.
        // По замерам GPU заметно ровнее на 240 Гц (95% кадров ≤5,3 мс против 8,8 мс программно).
        if (Environment.GetEnvironmentVariable("ISLAND_GPU") == "0") _s.SoftwareRendering = true;
        if (_s.SoftwareRendering && src?.CompositionTarget != null) src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
    }

    /// <summary>Закрытие по повторному запуску exe: глазки засыпают, «Пока!» — и выход.</summary>
    public void Goodbye()
    {
        if (_hiddenByFullscreen || _userHidden || _asleep) { Application.Current.Shutdown(); return; }
        Collapse();
        Toast("Пока!", null, Mood.Sleep, 5);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        t.Tick += (_, _) => { t.Stop(); Application.Current.Shutdown(); };
        t.Start();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        // WM_DPICHANGED: остров сам ставит размер/позицию — не даём WPF двигать окно
        if (msg == 0x02E0) { Dispatcher.BeginInvoke(() => PlaceWindow(CurrentBox()), DispatcherPriority.Render); }
        return IntPtr.Zero;
    }

    /// <summary>Тестовый режим: рисует все вкладки в PNG за пределами экрана и выходит.</summary>
    public string? SnapDir;

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MascotSmall.Visibility = _s.Mascot ? Visibility.Visible : Visibility.Collapsed;
        PlaceWindow(new Size(96, 32));
        if (SnapDir != null) { if (Environment.GetEnvironmentVariable("ISLAND_PREWARM") != "0") Prewarm(); await RunSnap(SnapDir); return; }
        _ = Dispatcher.BeginInvoke(Prewarm, DispatcherPriority.ApplicationIdle);

        _media.Changed += OnMediaChanged;
        await _media.InitAsync();

        _agent = new AgentServer(_s.AgentPort, Dispatcher);
        _agent.Changed += OnAgentChanged;
        _agent.Start();

        _fs = new FullscreenWatcher(_hwnd);
        _fs.Changed += OnFullscreenChanged;

        _host.ClipboardChanged += OnClipboardChanged;
        _ctl.VolumeChanged += (v, m) => Dispatcher.BeginInvoke(() => SetVolumeUi(v, m));
        RegisterHotkeys();
        if (_s.VolumeIconMixer) _volHook = new VolumeIconHook(() => Dispatcher.BeginInvoke(OpenFlyout));
        InitMic();
        LyricsOnMedia();
        InitUpdater();
        InitNet();

        if (StartHidden || _hideRequested) { _hideRequested = false; HideIsland(); return; }
        ShowUpdatedToast();
        _trimTimer.Start();
        _loadedDone = true;
        StartIdleWatch();
    }

    public bool StartHidden;
    bool _loadedDone, _hideRequested;

    public void RegisterHotkeys()
    {
        _host.ClearHotkeys();
        var failed = new System.Collections.Generic.List<string>();
        if (!_host.RegisterHotkey(_s.HotkeyShelf, () => ToggleFromHotkey("shelf"))) failed.Add(_s.HotkeyShelf);
        if (!_host.RegisterHotkey(_s.HotkeyChat, () => ToggleFromHotkey("chat"))) failed.Add(_s.HotkeyChat);
        if (!_host.RegisterHotkey(_s.HotkeyScreenshot, () => Act_Region(this, new RoutedEventArgs()))) failed.Add(_s.HotkeyScreenshot);
        if (!string.IsNullOrWhiteSpace(_s.HotkeySystem) && !_host.RegisterHotkey(_s.HotkeySystem, () => ToggleFromHotkey("system"))) failed.Add(_s.HotkeySystem);
        if (!string.IsNullOrWhiteSpace(_s.HotkeyMic) && !_host.RegisterHotkey(_s.HotkeyMic, ToggleMic)) failed.Add(_s.HotkeyMic);
        if (failed.Count > 0) Dispatcher.BeginInvoke(() => Toast("Занято: " + string.Join(", ", failed), "", Mood.Alert, 4), DispatcherPriority.Background);
    }

    void ToggleFromHotkey(string page)
    {
        if (_expanded && _page == page) { Collapse(); return; }
        if (_hiddenByFullscreen) Show();
        Expand(page, activate: true);
    }

    // =====================================================================
    //  Геометрия окна: окно = остров + небольшой запас. Прозрачное окно маленькое → дёшево.
    // =====================================================================
    (Native.RECT b, double scale) Monitor()
    {
        var mon = Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, Native.MONITOR_DEFAULTTOPRIMARY);
        var (b, _, s, _) = Native.MonitorOf(mon);
        return (b, s);
    }

    Size CurrentBox() => new(Math.Max(_target.Width, Shell.ActualWidth), Math.Max(_target.Height, Shell.ActualHeight));

    void PlaceWindow(Size shell)
    {
        if (_hwnd == IntPtr.Zero) return;
        var (b, s) = Monitor();
        // Ширина окна всегда одна (под раскрытый остров): тогда X окна не меняется. Иначе при SetWindowPos слоёное окно
        // на один кадр показывает старую картинку на новом месте — капсула «мигает» сбоку (тот самый 25-й кадр).
        // Прозрачные пиксели клики не ловят, так что широкое окно ничему не мешает.
        int w = (int)Math.Ceiling((Math.Max(shell.Width, ExpandedBox.Width) + Pad * 2) * s);
        int h = (int)Math.Ceiling((shell.Height + Pad + 6) * s);
        int x = b.Left + (b.W - w) / 2;
        // одинаковый SetWindowPos слоёного окна всё равно вызывает перерисовку и дёрганье — пропускаем
        if (x == _placed.X && b.Top == _placed.Y && w == _placed.W && h == _placed.H) return;
        _placed = (x, b.Top, w, h);
        if (SnapDir != null) { Native.SetWindowPos(_hwnd, IntPtr.Zero, -6000, -3000, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER); return; }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, b.Top, w, h, Native.SWP_NOACTIVATE);
    }

    (int X, int Y, int W, int H) _placed;
    // раскрытый остров живёт в окне одного размера → при смене вкладок окно не двигается и не мигает
    static readonly Size ExpandedBox = new(690 * 1.08, 470 * 1.1);

    void AnimateTo(Size target, bool expanded)
    {
        var from = new Size(Shell.ActualWidth > 0 ? Shell.ActualWidth : _target.Width, Shell.ActualHeight > 0 ? Shell.ActualHeight : _target.Height);
        if (Math.Abs(from.Width - target.Width) < 0.5 && Math.Abs(from.Height - target.Height) < 0.5 && _target == target) return;
        // Уже едем ровно туда же — не перезапускаем. Иначе при смене трека (плеер шлёт 5–10 событий подряд)
        // сворачивание каждый раз начиналось заново с нулевой скоростью: медленно и рывками.
        if (_target == target && Environment.TickCount64 < _animUntil) return;
        _target = target;
        bool wasExpanded = from.Height > 100;
        // на время анимации окно — с запасом под «пружину»
        if (expanded || wasExpanded) PlaceWindow(ExpandedBox);
        else PlaceWindow(new Size(Math.Max(from.Width, target.Width) * 1.08, Math.Max(from.Height, target.Height) * 1.1));

        if (expanded)
        {
            // содержимое сразу получает итоговый размер вкладки: пока оболочка растёт, оно не перекомпоновывается
            // на каждом кадре, а просто «открывается» обрезкой
            Expanded.Width = target.Width - 32;
            Expanded.Height = target.Height - 26;
        }
        // вся тяжёлая раскладка (новая вкладка, строки микшера) — ДО старта анимации, а не в её первом кадре
        Shell.UpdateLayout();

        bool grow = target.Width * target.Height > from.Width * from.Height;
        IEasingFunction ease;
        TimeSpan dur;
        // Пружина вместо BackEase/Quintic: старт без рывка, мягкая остановка. Основное движение — первые ~45%
        // длительности, дальше плавный «доезд», поэтому длительности больше, а ощущается быстро.
        if (expanded && wasExpanded)
        {
            // вкладка → вкладка: без отскока
            ease = Theme.Spring(1.0, 7.0);
            dur = Theme.Ms(520);
        }
        else if (expanded)
        {
            ease = Theme.Spring(0.8, 8.5); // раскрытие — быстрый старт, лёгкий отскок
            dur = Theme.Ms(520);
        }
        else
        {
            ease = Theme.Spring(grow ? 0.8 : 0.92, 7.2); // сворачивание и смена ширины капсулы
            dur = Theme.Ms(grow ? 600 : 560);
        }

        double r = expanded ? 32 : Math.Min(16, target.Height / 2);
        int gen = ++_animGen;
        _animUntil = Environment.TickCount64 + (long)dur.TotalMilliseconds + 80;
        void Go()
        {
            if (gen != _animGen) return; // за это время уже попросили другую анимацию
            var aw = new DoubleAnimation(target.Width, dur) { EasingFunction = ease };
            var ah = new DoubleAnimation(target.Height, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(aw, Fps); Timeline.SetDesiredFrameRate(ah, Fps);
            ah.Completed += (_, _) => { if (_target == target && !_expanded) PlaceWindow(target); };
            Shell.BeginAnimation(WidthProperty, aw);
            Shell.BeginAnimation(HeightProperty, ah);
            var ar = new DoubleAnimation(r, dur) { EasingFunction = Theme.Spring(1.0, 7.0) };
            Timeline.SetDesiredFrameRate(ar, Fps);
            ShellBg.BeginAnimation(System.Windows.Shapes.Rectangle.RadiusXProperty, ar);
            ShellBg.BeginAnimation(System.Windows.Shapes.Rectangle.RadiusYProperty, ar);
        }
        // Первый кадр с новой вкладкой тяжёлый (растеризация текста, иконок). Если запустить анимацию сразу,
        // её часы уйдут вперёд на этот кадр, и оболочка «прыгнет». Поэтому стартуем, когда тяжёлый кадр уже показан.
        AfterFrames(2, Go);
    }

    int _animGen;
    long _animUntil; // до какого момента идёт анимация размера (тяжёлую работу откладываем на потом)
    static void AfterFrames(int n, Action a)
    {
        TimeSpan last = TimeSpan.Zero;
        EventHandler? h = null;
        h = (_, e) =>
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            if (t == last) return;
            last = t;
            if (--n > 0) return;
            CompositionTarget.Rendering -= h;
            a();
        };
        CompositionTarget.Rendering += h;
    }

    /// <summary>Один раз после запуска раскладываем все вкладки невидимо — первое открытие уже не тормозит.</summary>
    void Prewarm()
    {
        if (_expanded) return;
        try
        {
            Expanded.Width = SizeSound.Width - 32; Expanded.Height = SizeSound.Height - 26;
            Expanded.Opacity = 0; Expanded.Visibility = Visibility.Visible;
            BuildSystem();
            UIElement[] pages = { PageHome, PageChat, PageControl, PageShelf, PageAgent, PageSound, PageSystem };
            var vis = new Visibility[pages.Length];
            for (int i = 0; i < pages.Length; i++) vis[i] = pages[i].Visibility;
            foreach (var pg in pages)
            {
                foreach (var o in pages) o.Visibility = ReferenceEquals(o, pg) ? Visibility.Visible : Visibility.Collapsed;
                if (ReferenceEquals(pg, PageSound)) { PageSound.Start(); PageSound.Stop(); }
                Expanded.UpdateLayout();
            }
            for (int i = 0; i < pages.Length; i++) pages[i].Visibility = vis[i];
            if (!_pinnedLoaded) LoadPinned(); // иконки приложений — тоже заранее, а не при первом раскрытии
            PrepareGlow();
        }
        catch { }
        finally { if (!_expanded) Expanded.Visibility = Visibility.Collapsed; }
    }

    void Hover(bool on)
    {
        var a = new DoubleAnimation(on ? 1.05 : 1, Theme.Ms(460)) { EasingFunction = Theme.Spring(0.7, 7.5) };
        Timeline.SetDesiredFrameRate(a, Fps);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // =====================================================================
    //  Развернуть / свернуть
    // =====================================================================
    void Shell_Click(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        if (_rec.IsRecording && e.OriginalSource is DependencyObject d && IsInside(d, RecStopBtn)) return;
        if (_toastText != null && _toastClick != null)
        {
            var act = _toastClick; _toastClick = null; _toastText = null; _toastTimer.Stop();
            UpdateCompact(); act();
            return;
        }
        string page = _agent?.Pending != null ? "agent" : _page;
        Expand(page, activate: true);
    }

    static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x == parent) return true;
        return false;
    }

    public void Expand(string page, bool activate = false)
    {
        if (_userHidden) ShowIsland();
        Wake();
        SetPassing(false);
        _collapseTimer.Stop();
        _trimTimer.Stop();
        Hover(false);
        bool wasExpanded = _expanded;
        _expanded = true;
        SelectPage(page);

        if (!wasExpanded)
        {
            Fade(Compact, 0, 110, () => Compact.Visibility = Visibility.Collapsed);
            Expanded.Visibility = Visibility.Visible;
            Fade(Expanded, 1, 220, null);
            Shell.Cursor = Cursors.Arrow;
        }
        AnimateTo(PageSize(page), true);
        UpdateTimers();
        if (activate)
        {
            Native.SetForegroundWindow(_hwnd);
            Activate();
            if (page == "chat") Dispatcher.BeginInvoke(() => { ChatInput.Focus(); Keyboard.Focus(ChatInput); }, DispatcherPriority.Input);
        }
    }

    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _autoExpanded = false;
        _collapseTimer.Stop();
        PageSound.Stop();
        StopSystem();
        Keyboard.ClearFocus();
        Fade(Expanded, 0, 150, () => Expanded.Visibility = Visibility.Collapsed);
        Compact.Visibility = Visibility.Visible;
        Fade(Compact, 1, 280, null, 120);
        Shell.Cursor = Cursors.Hand;
        UpdateCompact(force: true);
        UpdateTimers();
        if (_hiddenByFullscreen) Hide();
        _trimTimer.Start();
    }

    static void Fade(UIElement el, double to, int ms, Action? done, int delay = 0)
    {
        var a = new DoubleAnimation(to, Theme.Ms(ms)) { BeginTime = Theme.Ms(delay), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(a, Fps);
        if (done != null) a.Completed += (_, _) => { if (Math.Abs(el.Opacity - to) < 0.01) done(); };
        el.BeginAnimation(OpacityProperty, a);
    }

    Size PageSize(string p) => p switch
    {
        "chat" => SizeChat,
        "control" => SizeControl,
        "shelf" => SizeShelf,
        "agent" => SizeAgent,
        "sound" => SizeSound,
        "system" => SizeSystem,
        _ => SizeHome,
    };

    void SelectPage(string page)
    {
        _page = page;
        var tab = page switch { "chat" => TabChat, "sound" => TabSound, "control" => TabControl, "shelf" => TabShelf, "agent" => TabAgent, "system" => TabSystem, _ => TabHome };
        if (tab.IsChecked != true) tab.IsChecked = true; else ShowPage(page);
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string p }) { _page = p; ShowPage(p); if (_expanded) AnimateTo(PageSize(p), true); }
    }

    void ShowPage(string p)
    {
        PageHome.Visibility = p == "home" ? Visibility.Visible : Visibility.Collapsed;
        PageChat.Visibility = p == "chat" ? Visibility.Visible : Visibility.Collapsed;
        PageControl.Visibility = p == "control" ? Visibility.Visible : Visibility.Collapsed;
        PageShelf.Visibility = p == "shelf" ? Visibility.Visible : Visibility.Collapsed;
        PageAgent.Visibility = p == "agent" ? Visibility.Visible : Visibility.Collapsed;
        PageSound.Visibility = p == "sound" ? Visibility.Visible : Visibility.Collapsed;
        PageSystem.Visibility = p == "system" ? Visibility.Visible : Visibility.Collapsed;
        if (p == "sound") PageSound.Start(); else PageSound.Stop();
        if (p == "system") StartSystem(); else StopSystem();
        if (_expanded)
        {
            UIElement pg = p switch { "chat" => PageChat, "control" => PageControl, "shelf" => PageShelf, "agent" => PageAgent, "sound" => PageSound, "system" => PageSystem, _ => PageHome };
            pg.Opacity = 0;
            Fade(pg, 1, 280, null); // без задержки: тяжёлый первый кадр слоя прозрачности — до начала движения, а не посреди него
        }
        switch (p)
        {
            case "home": RefreshHome(); break;
            case "control": _ = RefreshControlAsync(); break;
            case "shelf": RefreshShelf(); break;
            case "agent": RefreshAgent(); break;
            case "chat": if (IsActive) Dispatcher.BeginInvoke(() => ChatInput.Focus(), DispatcherPriority.Input); break;
        }
        UpdateTimers();
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinBtn.Content = _pinned ? "" : "";
        PinBtn.Foreground = _pinned ? (Brush)FindResource("Accent") : Brushes.White;
    }

    // =====================================================================
    //  Компактный вид — что показывать (по приоритету)
    // =====================================================================
    void UpdateCompact(bool force = false)
    {
        MascotSmall.Visibility = CoverSmall.Visibility = RecDot.Visibility = CompactGlyph.Visibility = Visibility.Collapsed;
        Eq.Visibility = CompactRight.Visibility = RecStopBtn.Visibility = Visibility.Collapsed;
        CompactText.Foreground = Brushes.White;
        CompactText.FontWeight = FontWeights.SemiBold;
        double minW = 140;
        Mood mood = Mood.Idle;
        bool eq = false, idle = false, fixedW = false;
        _capsuleLyrics = false; _capsuleDownload = false;
        CompactText.BeginAnimation(OpacityProperty, null); CompactText.Opacity = 1;
        CompactText.Visibility = Visibility.Visible;

        var a = _agent;
        if (_toastText != null)
        {
            if (_toastGlyph != null) { CompactGlyph.Text = _toastGlyph; CompactGlyph.Visibility = Visibility.Visible; }
            else ShowMascot();
            mood = _toastMood;
            CompactText.Text = _toastText;
        }
        else if (a?.Pending != null)
        {
            ShowMascot(); mood = Mood.Alert;
            CompactText.Text = a.Pending.Kind == "question" ? "Вопрос от " + a.Source : a.Source + " ждёт разрешения";
            CompactRight.Text = "нажми"; CompactRight.Visibility = Visibility.Visible;
        }
        else if (_rec.IsRecording)
        {
            RecDot.Visibility = Visibility.Visible;
            CompactText.Text = (DateTime.Now - _rec.StartedAt).ToString(@"m\:ss");
            CompactText.FontWeight = FontWeights.Normal;
            RecStopBtn.Visibility = Visibility.Visible;
            minW = 170;
        }
        else if (a != null && a.Status is AgentStatus.Working or AgentStatus.Waiting or AgentStatus.Done)
        {
            ShowMascot();
            mood = a.Status switch { AgentStatus.Working => Mood.Thinking, AgentStatus.Done => Mood.Happy, _ => Mood.Alert };
            CompactText.Text = a.Status == AgentStatus.Done ? "Готово" : a.Detail;
            CompactText.FontWeight = a.Status == AgentStatus.Done ? FontWeights.SemiBold : FontWeights.Normal;
            if (a.Project.Length > 0) { CompactRight.Text = a.Project; CompactRight.Visibility = Visibility.Visible; }
            minW = 220;
        }
        else if (_chat.Busy)
        {
            ShowMascot(); mood = Mood.Thinking;
            CompactText.Text = "Думаю…"; CompactText.FontWeight = FontWeights.Normal;
        }
        else if (ShownDownload() != null)
        {
            CompactGlyph.Text = ""; CompactGlyph.Visibility = Visibility.Visible;
            CompactText.Text = DownloadLine();
            CompactText.FontWeight = FontWeights.Normal;
            _capsuleDownload = true;
            minW = 370; fixedW = true;
        }
        else if (MusicShown())
        {
            if (_media.Info.Cover != null) { CoverSmallBrush.ImageSource = _media.Info.Cover; CoverSmall.Visibility = Visibility.Visible; }
            else if (CoverSmallBrush.ImageSource != null && !(_media.Info.IsPlaying && _media.Info.HasMedia)) CoverSmall.Visibility = Visibility.Visible; // смена трека: старая обложка, пока не пришла новая
            else { CompactGlyph.Text = ""; CompactGlyph.Visibility = Visibility.Visible; }
            CompactText.Visibility = Visibility.Collapsed;
            Eq.Visibility = Visibility.Visible; eq = true;
            minW = 108; // при музыке/видео — компактно: обложка + эквалайзер
            if (_s.LyricsInCapsule && (_lyr?.Synced == true || (_lyrState == LyrState.Loading && _lyrKeepWide)))
            {
                // строчка песни: ширина постоянная, чтобы капсула не «дышала» на каждой строке
                _capsuleLyrics = true;
                if (_lyrIdx == -2 && _lyr != null) { var p = _media.Info.CurrentPosition + TimeSpan.FromSeconds(LyricLeadSec); _lyrIdx = _lyr.Lines.FindLastIndex(x => x.At <= p); }
                CompactText.Text = CurrentLyric();
                CompactText.FontWeight = FontWeights.Normal;
                CompactText.Visibility = Visibility.Visible;
                minW = 330; fixedW = true;
            }
        }
        else
        {
            // покой: только глазки, капсула минимальная
            ShowMascot();
            idle = true;
        }

        MascotSmall.Mood = mood;
        if (!_expanded) MascotBig.Mood = mood;
        SetEq(eq && !_expanded && IsVisible);

        if (idle) CompactText.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(CompactLeft, idle ? 3 : 1);
        CompactLeft.HorizontalAlignment = idle ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        UpdateMicMark();
        LyricsTick();
        if (_expanded && !force) return;
        CompactText.MaxWidth = 300;
        Compact.Measure(new Size(double.PositiveInfinity, 32));
        double w = idle ? 84 : fixedW ? minW : Math.Max(minW, Compact.DesiredSize.Width + 22);
        if ((idle || fixedW) && MicMark.Visibility == Visibility.Visible) w += 14;
        if (!_expanded && !_hiddenByFullscreen) AnimateTo(new Size(Math.Ceiling(w), 32), false); // скрыт в игре — догоним при показе

        void ShowMascot() { if (_s.Mascot) MascotSmall.Visibility = Visibility.Visible; }
    }

    // При смене трека плеер на мгновение сообщает «ничего не играет» — капсула не должна схлопываться в глазки и обратно.
    const int MusicGraceMs = 1500;
    long _musicSeen;
    DispatcherTimer? _musicGrace;
    bool MusicShown()
    {
        long now = Environment.TickCount64;
        if (_media.Info.IsPlaying && _media.Info.HasMedia) { _musicSeen = now; _musicGrace?.Stop(); return true; }
        if (_musicSeen == 0 || now - _musicSeen >= MusicGraceMs) return false;
        if (_musicGrace == null)
        {
            _musicGrace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MusicGraceMs + 50) };
            _musicGrace.Tick += (_, _) => { _musicGrace.Stop(); if (!_expanded) UpdateCompact(); };
        }
        _musicGrace.Stop(); _musicGrace.Start();
        return true;
    }

    public void Toast(string text, string? glyph = null, Mood mood = Mood.Happy, double seconds = 2.5)
    {
        _toastText = text; _toastGlyph = glyph; _toastMood = mood;
        _toastClick = null; // действие по клику — только у того уведомления, которое его задало (после вызова Toast)
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
        if (_hiddenByFullscreen || _userHidden) return;
        Wake(); // уведомление будит остров; когда исчезнет — снова уснёт, если мышь так и не трогали
        UpdateCompact();
        if (_expanded) MascotBig.Mood = mood;
    }

    // эквалайзер — анимируется только когда реально виден
    bool _eqOn;
    void SetEq(bool on)
    {
        on &= _s.AnimateEqualizer;
        if (on == _eqOn) return;
        _eqOn = on;
        var bars = new[] { Eq1, Eq2, Eq3, Eq4 };
        var dur = new[] { 420, 310, 520, 370 };
        for (int i = 0; i < 4; i++)
        {
            var st = (ScaleTransform)bars[i].RenderTransform;
            if (!on) { st.BeginAnimation(ScaleTransform.ScaleYProperty, null); continue; }
            var a = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(dur[i])) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(a, 24);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    // =====================================================================
    //  Таймеры — работают только когда нужны
    // =====================================================================
    void UpdateTimers()
    {
        bool need = (_rec.IsRecording && !_expanded) || (_expanded && _page == "home" && _media.Info.IsPlaying);
        if (need) _tick.Start(); else _tick.Stop();
        if (_expanded) SetEq(false); else UpdateCompactEqOnly();
    }

    void UpdateCompactEqOnly() => SetEq(Eq.Visibility == Visibility.Visible && IsVisible);

    void OnTick()
    {
        if (_rec.IsRecording && !_expanded && _toastText == null && _agent?.Pending == null)
            CompactText.Text = (DateTime.Now - _rec.StartedAt).ToString(@"m\:ss");
        if (_expanded && _page == "home") UpdateProgress();
    }

    // =====================================================================
    //  Полноэкранный режим
    // =====================================================================
    void OnFullscreenChanged(bool fs)
    {
        if (_userHidden) return;
        // В игре мышь не должна проходить через наш хук (он нужен только для значка громкости на панели задач,
        // а её в полноэкранной игре всё равно не видно) — снимаем его, чтобы не добавлять ни микросекунды к вводу.
        if (fs) { _volHook?.Dispose(); _volHook = null; }
        else if (_s.VolumeIconMixer && _volHook == null) _volHook = new VolumeIconHook(() => Dispatcher.BeginInvoke(OpenFlyout));
        if (!_s.HideInFullscreen) return;
        if (fs && !_expanded && !_rec.IsRecording)
        {
            _hiddenByFullscreen = true;
            SetEq(false);
            _idleTimer.Stop(); // в игре курсор не опрашиваем совсем
            Hide();
        }
        else if (!fs && _hiddenByFullscreen)
        {
            _hiddenByFullscreen = false;
            StartIdleWatch();
            if (_asleep) return; // спит — покажется при движении мыши
            Show();
            PlaceWindow(_target);
            UpdateCompact(force: true);
        }
    }

    // =====================================================================
    //  Агенты (Claude Code / opencode)
    // =====================================================================
    AgentRequest? _shownRequest;
    void OnAgentChanged()
    {
        var a = _agent!;
        bool active = a.Source.Length > 0;
        TabAgent.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        TabAgentText.Text = " " + (a.Source.Length > 0 ? a.Source : "Агент");
        TabAgent.ToolTip = a.Source.Length > 0 ? a.Source : "Агент"; // на вкладке только иконка — иначе не помещается

        if (a.Status == AgentStatus.Done) { _agentDoneTimer.Stop(); _agentDoneTimer.Start(); }

        // новый запрос — раскрываемся сами (без кражи фокуса)
        if (_userHidden) return; // остров скрыт пользователем — агент спросит в терминале
        if (a.Pending != null && a.Pending != _shownRequest)
        {
            _shownRequest = a.Pending;
            if (_hiddenByFullscreen) { Show(); _hiddenByFullscreen = false; StartIdleWatch(); }
            Expand("agent");
        }
        else if (a.Pending == null && _shownRequest != null)
        {
            _shownRequest = null;
            if (_expanded && _page == "agent" && !Shell.IsMouseOver && !_pinned) Collapse();
        }

        if (_expanded && _page == "agent") RefreshAgent();
        UpdateCompact();
    }

    void RefreshAgent()
    {
        var a = _agent;
        if (a == null) return;
        AgentSource.Text = a.Source.Length > 0 ? a.Source : "Агенты не подключены";
        AgentProject.Text = a.Project;
        AgentDetail.Text = a.Source.Length == 0
            ? "Открой ⚙ Настройки → «Подключить Claude Code» / «Подключить opencode», и остров будет показывать, что делает агент."
            : a.Status switch { AgentStatus.Working => "⏳ " + a.Detail, AgentStatus.Done => "✓ Готово", AgentStatus.Waiting => "⚠ " + a.Detail, _ => a.Detail };
        MascotBig.Mood = a.Status switch { AgentStatus.Working => Mood.Thinking, AgentStatus.Done => Mood.Happy, AgentStatus.Waiting => Mood.Alert, _ => Mood.Idle };

        var req = a.Pending;
        AgentReqBox.Visibility = req != null ? Visibility.Visible : Visibility.Collapsed;
        AgentReqButtons.Children.Clear();
        if (req == null) return;
        AgentReqTitle.Text = req.Title;
        AgentReqDetail.Text = req.Detail;
        AgentReqDetail.Visibility = req.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (req.Kind == "permission")
        {
            AgentReqButtons.Children.Add(ChoiceButton("Разрешить", "allow", true));
            AgentReqButtons.Children.Add(ChoiceButton("Отклонить", "deny", false));
        }
        else foreach (var o in req.Options) AgentReqButtons.Children.Add(ChoiceButton(o, o, false));
        AgentReqButtons.Children.Add(ChoiceButton("В терминале", null, false, dim: true));
    }

    Button ChoiceButton(string text, string? value, bool primary, bool dim = false)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12.5, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
            Style = (Style)FindResource("Tile"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 6, 6),
            Background = primary ? (Brush)FindResource("Accent") : (Brush)FindResource("Tab"),
            Foreground = dim ? (Brush)FindResource("Dim") : Brushes.White,
        };
        b.Click += (_, _) => { _agent?.Answer(value); Toast(value == null ? "Ответь в терминале" : "Ответ отправлен", null, Mood.Happy, 1.6); };
        return b;
    }

    // =====================================================================
    //  Меню, настройки, выход
    // =====================================================================
    public void ShowMenu()
    {
        var m = new ContextMenu();
        void Item(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Item("Открыть остров", () => Expand("home", true));
        Item(_userHidden ? "Показать остров" : "Скрыть остров", ToggleHidden);
        Item("Настройки…", OpenSettings);
        Item("Папка снимков и записей", () => ControlService.OpenUri(EnsureFolder()));
        m.Items.Add(new Separator());
        Item(_upd?.Ready is { } nv ? $"Установить версию {Updater.Show(nv)}" : $"Проверить обновления (версия {Updater.Show(Updater.Current)})", () => _ = CheckUpdate(manual: true));
        Item("Выход", () => Application.Current.Shutdown());
        Native.SetForegroundWindow(_hwnd);
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        m.IsOpen = true;
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // =====================================================================
    //  «Скрыть остров»: окно не рисуется, и всё фоновое отключено — нагрузка ~0
    // =====================================================================
    public void ToggleHidden()
    {
        if (!_loadedDone && !_userHidden) { _hideRequested = !_hideRequested; return; } // ещё запускаемся — скроемся по окончании
        if (_userHidden) ShowIsland(); else HideIsland();
    }

    public void HideIsland()
    {
        if (_userHidden) return;
        _userHidden = true;
        _idleTimer.Stop();
        Wake(); // при _userHidden только сбрасывает позицию
        Collapse();
        _flyout?.Close2();
        _tick.Stop(); _toastTimer.Stop(); _collapseTimer.Stop();
        _toastText = null;
        SetEq(false);
        PageSound.Stop();
        StopSystem();
        _volHook?.Dispose(); _volHook = null;         // перехват мыши у значка громкости
        if (_fs != null) { _fs.Changed -= OnFullscreenChanged; _fs.Dispose(); _fs = null; } // слежка за окнами
        _host.ClearHotkeys();
        Hide();
        _trimTimer.Stop();
        Native.TrimMemory();
    }

    public void ShowIsland()
    {
        if (!_userHidden) return;
        _userHidden = false;
        _loadedDone = true;
        _hiddenByFullscreen = false;
        _fs = new FullscreenWatcher(_hwnd);
        _fs.Changed += OnFullscreenChanged;
        RegisterHotkeys();
        if (_s.VolumeIconMixer) _volHook = new VolumeIconHook(() => Dispatcher.BeginInvoke(OpenFlyout));
        Show();
        _placed = default;
        PlaceWindow(_target);
        UpdateCompact(force: true);
        UpdateTimers();
        StartIdleWatch();
    }

    SettingsWindow? _settingsWnd;
    void OpenSettings()
    {
        Collapse();
        if (_settingsWnd != null) { _settingsWnd.Activate(); return; }
        _settingsWnd = new SettingsWindow(_s, this);
        _settingsWnd.Closed += (_, _) => { _settingsWnd = null; Native.TrimMemory(); };
        _settingsWnd.Show();
        _settingsWnd.Activate();
    }

    public void ApplySettings()
    {
        Theme.Apply(_s);
        PageSound.Fps = Fps;
        RegisterHotkeys();
        MascotSmall.Visibility = _s.Mascot ? Visibility.Visible : Visibility.Collapsed;
        _pinnedLoaded = false;
        if (_loadedDone) { LyricsOnMedia(); RebuildLyricsView(); UpdateMicButton(); InitNet(); }
        UpdateCompact(force: true);
        if (_loadedDone && !_userHidden) StartIdleWatch();
    }

    /// <summary>Кнопка в настройках: раскрыть и свернуть остров, чтобы увидеть выбранный характер анимаций.</summary>
    public void PreviewAnimation()
    {
        if (_userHidden) return;
        Wake();
        if (_expanded) { Collapse(); return; }
        Expand("home");
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        t.Tick += (_, _) => { t.Stop(); if (!Shell.IsMouseOver && !_pinned) Collapse(); };
        t.Start();
    }

    /// <summary>Микшер над треем (клик по значку громкости Windows).</summary>
    public void OpenFlyout()
    {
        _flyout ??= new MixerFlyout(_mixer);
        _flyout.Toggle();
    }

    string EnsureFolder() { System.IO.Directory.CreateDirectory(_s.CaptureFolder); return _s.CaptureFolder; }

    protected override void OnClosed(EventArgs e)
    {
        _agent?.Dispose();
        _fs?.Dispose();
        _volHook?.Dispose();
        _mic?.Dispose();
        _dl?.Dispose(); _net?.Dispose();
        _mixer.Dispose();
        _ctl.Dispose();
        base.OnClosed(e);
    }
}
