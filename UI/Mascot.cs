using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Island.UI;

public enum Mood { Idle, Thinking, Happy, Alert, Sleep }

/// <summary>
/// «Живой характер» острова: два глаза. Моргают (таймер работает только когда маскот виден),
/// думают (бегают глазами), радуются (^ ^), настораживаются, спят.
/// </summary>
public sealed class Mascot : Viewbox
{
    static readonly Random Rnd = new();
    readonly Border _l, _r;
    readonly Path _happy, _sleep;
    readonly ScaleTransform _ls = new(), _rs = new();
    readonly TranslateTransform _lt = new(), _rt = new();
    readonly DispatcherTimer _blink = new(DispatcherPriority.Background);
    Mood _mood = Mood.Idle;

    public Mascot()
    {
        var c = new Canvas { Width = 26, Height = 16 };
        _l = Eye(_ls, _lt); _r = Eye(_rs, _rt);
        Canvas.SetLeft(_l, 6.5); Canvas.SetTop(_l, 4);
        Canvas.SetLeft(_r, 14); Canvas.SetTop(_r, 4);
        _happy = new Path
        {
            Data = Geometry.Parse("M6.5,10 Q9,4.5 11.5,10 M14,10 Q16.5,4.5 19,10"),
            Stroke = Brushes.White, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed,
        };
        _sleep = new Path
        {
            Data = Geometry.Parse("M6.5,9 L11.5,9 M14,9 L19,9"),
            Stroke = Brushes.White, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed,
        };
        c.Children.Add(_l); c.Children.Add(_r); c.Children.Add(_happy); c.Children.Add(_sleep);
        Child = c;
        Stretch = Stretch.Uniform;

        _blink.Tick += (_, _) => { Blink(); _blink.Interval = TimeSpan.FromMilliseconds(Rnd.Next(2500, 6500)); };
        _blink.Interval = TimeSpan.FromSeconds(3);
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    static Border Eye(ScaleTransform s, TranslateTransform t) => new()
    {
        Width = 5.5, Height = 8, CornerRadius = new CornerRadius(2.75), Background = Brushes.White,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new TransformGroup { Children = { s, t } },
    };

    public Mood Mood
    {
        get => _mood;
        set { if (_mood == value) return; _mood = value; Apply(); }
    }

    void UpdateTimer()
    {
        if (IsVisible && _mood is Mood.Idle or Mood.Alert) _blink.Start(); else _blink.Stop();
    }

    void Apply()
    {
        foreach (var tr in new Animatable[] { _ls, _rs, _lt, _rt })
        {
            tr.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            tr.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            tr.BeginAnimation(TranslateTransform.XProperty, null);
            tr.BeginAnimation(TranslateTransform.YProperty, null);
        }
        _ls.ScaleY = _rs.ScaleY = 1; _ls.ScaleX = _rs.ScaleX = 1;
        _lt.X = _rt.X = _lt.Y = _rt.Y = 0;

        bool eyes = _mood is Mood.Idle or Mood.Thinking or Mood.Alert;
        _l.Visibility = _r.Visibility = eyes ? Visibility.Visible : Visibility.Collapsed;
        _happy.Visibility = _mood == Mood.Happy ? Visibility.Visible : Visibility.Collapsed;
        _sleep.Visibility = _mood == Mood.Sleep ? Visibility.Visible : Visibility.Collapsed;

        switch (_mood)
        {
            case Mood.Thinking:
            {
                // смотрит вверх и водит глазами — «задумался»
                var look = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2.4) };
                look.KeyFrames.Add(new EasingDoubleKeyFrame(-2.2, KeyTime.FromPercent(0.2), new SineEase()));
                look.KeyFrames.Add(new EasingDoubleKeyFrame(-2.2, KeyTime.FromPercent(0.45)));
                look.KeyFrames.Add(new EasingDoubleKeyFrame(2.2, KeyTime.FromPercent(0.7), new SineEase()));
                look.KeyFrames.Add(new EasingDoubleKeyFrame(2.2, KeyTime.FromPercent(0.95)));
                Timeline.SetDesiredFrameRate(look, 30);
                _lt.BeginAnimation(TranslateTransform.XProperty, look);
                _rt.BeginAnimation(TranslateTransform.XProperty, look);
                _lt.Y = _rt.Y = -1.5;
                _ls.ScaleY = _rs.ScaleY = 0.8;
                break;
            }
            case Mood.Alert:
            {
                var hop = new DoubleAnimation(0, -1.6, TimeSpan.FromMilliseconds(260)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3), EasingFunction = new QuadraticEase() };
                _lt.BeginAnimation(TranslateTransform.YProperty, hop);
                _rt.BeginAnimation(TranslateTransform.YProperty, hop);
                _ls.ScaleY = _rs.ScaleY = 1.12;
                break;
            }
            case Mood.Happy:
            {
                var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
                _happy.RenderTransformOrigin = new Point(0.5, 0.5);
                var st = new ScaleTransform();
                _happy.RenderTransform = st;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                break;
            }
        }
        UpdateTimer();
    }

    void Blink()
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(170) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromPercent(0.45)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1)));
        _ls.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        _rs.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        // иногда — быстрый взгляд в сторону
        if (Rnd.Next(4) == 0)
        {
            double dx = Rnd.Next(2) == 0 ? -1.8 : 1.8;
            var g = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1400) };
            g.KeyFrames.Add(new EasingDoubleKeyFrame(dx, KeyTime.FromPercent(0.15), new QuadraticEase()));
            g.KeyFrames.Add(new EasingDoubleKeyFrame(dx, KeyTime.FromPercent(0.8)));
            g.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new QuadraticEase()));
            _lt.BeginAnimation(TranslateTransform.XProperty, g);
            _rt.BeginAnimation(TranslateTransform.XProperty, g);
        }
    }
}
