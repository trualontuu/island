using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Island.Services;

namespace Island.UI;

/// <summary>Сообщения Telegram в капсуле: аватарка-буква, имя и текст; клик — открыть Telegram.</summary>
public partial class IslandWindow
{
    TelegramWatcher? _tg;
    TgMessage? _toastTg;   // капсула сейчас показывает это сообщение
    int _tgBurst;          // сколько сообщений пришло подряд, пока висит уведомление

    // цвета аватарок Telegram (по имени — всегда один и тот же цвет у одного человека)
    static readonly Color[] TgColors =
    {
        Color.FromRgb(0xFF, 0x51, 0x6A), Color.FromRgb(0xFF, 0xA8, 0x5C), Color.FromRgb(0xB6, 0x94, 0xF9), Color.FromRgb(0x9A, 0xD1, 0x64),
        Color.FromRgb(0x5B, 0xCB, 0xE3), Color.FromRgb(0x5C, 0xAF, 0xFA), Color.FromRgb(0xFF, 0x8A, 0xAC),
    };

    void InitTelegram()
    {
        if (SnapDir != null) return;
        if (_s.TelegramInCapsule && _tg == null)
        {
            _tg = new TelegramWatcher(Dispatcher);
            _tg.Message += OnTgMessage;
            _tg.Missed += OnTgMissed;
            _ = _tg.StartAsync();
            if (_userHidden || _hiddenByFullscreen) _tg.Pause(true);
        }
        else if (!_s.TelegramInCapsule && _tg != null) { _tg.Dispose(); _tg = null; }
        if (_tg != null) _tg.LoadAvatars = _s.TelegramAvatars;
    }

    void OnTgMessage(TgMessage m)
    {
        // пока висит прошлое сообщение — показываем новое и счётчик «+N»
        int burst = _toastTg != null && _toastText != null ? _tgBurst + 1 : 1;
        Toast(m.Title, null, Mood.Happy, Math.Clamp(6 + m.Text.Length / 20.0, 7, 12), m, burst);
        var app = m.AppId;
        _toastClick = () => TelegramWatcher.Open(app);
        TgAttention();
    }

    void OnTgMissed(int count, TgMessage? last)
    {
        if (last == null) return;
        if (count == 1) { OnTgMessage(last); return; }
        Toast($"Telegram: {count} {Plural(count, "новое сообщение", "новых сообщения", "новых сообщений")}", "", Mood.Happy, 6);
        var app = last.AppId;
        _toastClick = () => TelegramWatcher.Open(app);
        TgAttention();
    }

    static string Plural(int n, string one, string few, string many)
    {
        int a = Math.Abs(n) % 100, b = a % 10;
        return a is >= 11 and <= 19 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }

    static readonly Size TgBannerSize = new(440, 76);

    void FillTgBanner()
    {
        var m = _toastTg!;
        var name = m.Sender.Length > 0 ? m.Sender : m.Chat;
        EmojiText.Set(TgBigInitial, Initial(name));
        TgBigAvatar.Fill = new SolidColorBrush(TgColors[(int)(StableHash(name) % (uint)TgColors.Length)]);
        // настоящее фото профиля (если есть и включено) — поверх кружка с буквой
        bool photo = _s.TelegramAvatars && m.Avatar != null;
        TgBigPhotoBrush.ImageSource = photo ? m.Avatar : null;
        TgBigPhoto.Visibility = photo ? Visibility.Visible : Visibility.Collapsed;
        TgBigInitial.Visibility = photo ? Visibility.Collapsed : Visibility.Visible;
        EmojiText.Set(TgBigTitle, m.Title);
        TgBigMeta.Text = _tgBurst > 1 ? $"ещё {_tgBurst - 1}" : "сейчас";
        // «You have a new message» — Telegram скрывает текст в своих настройках
        bool hidden = !_s.TelegramShowText || m.Text.Length == 0 || m.Text.StartsWith("You have a new message", StringComparison.OrdinalIgnoreCase);
        EmojiText.Set(TgBigText, hidden ? "Новое сообщение" : m.Text);
    }

    /// <summary>Чтобы сообщение было заметно: рамка вспыхивает голубым (цвет Telegram), остров чуть «подпрыгивает».</summary>
    void TgAttention()
    {
        if (_hiddenByFullscreen || _userHidden || _expanded) return;
        var stroke = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
        ShellBg.Stroke = stroke;
        var flash = new System.Windows.Media.Animation.ColorAnimation(Color.FromArgb(0xFF, 0x2A, 0xAB, 0xEE), Theme.Ms(380))
        { AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(3), EasingFunction = new System.Windows.Media.Animation.SineEase() };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(flash, Fps);
        stroke.BeginAnimation(SolidColorBrush.ColorProperty, flash);
        var th = new System.Windows.Media.Animation.DoubleAnimation(2, Theme.Ms(380)) { AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(3) };
        th.Completed += (_, _) => { ShellBg.BeginAnimation(System.Windows.Shapes.Shape.StrokeThicknessProperty, null); ShellBg.StrokeThickness = 1; };
        ShellBg.BeginAnimation(System.Windows.Shapes.Shape.StrokeThicknessProperty, th);
        // «прыжок» — после того как карточка раскрылась
        var pop = new System.Windows.Media.Animation.DoubleAnimation(1, 1.06, Theme.Ms(170)) { AutoReverse = true, BeginTime = Theme.Ms(420), EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(pop, Fps);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    static string Initial(string name)
    {
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        // эмодзи в начале имени — берём его целиком (суррогатная пара)
        var e = System.Globalization.StringInfo.GetNextTextElement(name);
        return e.Length > 0 ? e : "?";
    }

    static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}
