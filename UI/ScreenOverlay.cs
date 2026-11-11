using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Island.Core;
using Island.Services;

namespace Island.UI;

public enum OverlayMode { Annotate, Select, Pick }

/// <summary>
/// Полноэкранный оверлей поверх замороженного снимка монитора. Окно непрозрачное (не layered) —
/// поэтому рисование и выделение не грузят систему.
/// </summary>
public sealed class ScreenOverlay : Window
{
    enum Tool { Pen, Arrow, Rect, Marker, Text }

    readonly OverlayMode _mode;
    readonly BitmapSource _shot;
    readonly Native.RECT _bounds;
    readonly double _scale;
    readonly Canvas _root = new();
    readonly Canvas _ink = new() { IsHitTestVisible = false };
    readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), IsHitTestVisible = false };
    readonly Rectangle _selBorder = new() { Stroke = Brushes.White, StrokeThickness = 1.2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Border _sizeTag = TagBox("");
    readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    readonly List<UIElement> _undo = new();

    Point _start; Rect _sel; bool _dragging, _editing;
    Tool _tool = Tool.Arrow;
    Color _color = Color.FromRgb(255, 59, 48);
    Shape? _cur; Point _curStart;

    // пипетка
    readonly Border _magnifier = new() { Width = 132, Height = 154, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)), BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), BorderThickness = new Thickness(1), IsHitTestVisible = false };
    readonly Image _magImg = new() { Width = 120, Height = 120, Stretch = Stretch.Fill };
    readonly TextBlock _magHex = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };

    readonly TaskCompletionSource<object?> _tcs = new();
    public Task<object?> Result => _tcs.Task;

    public ScreenOverlay(OverlayMode mode)
    {
        _mode = mode;
        var (b, s, _) = CaptureService.MonitorUnderCursor();
        _bounds = b; _scale = s;
        _shot = CaptureService.Capture(b.Left, b.Top, b.W, b.H);

        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left / s; Top = b.Top / s; Width = b.W / s; Height = b.H / s;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;

        double W = b.W / s, H = b.H / s;
        var img = new Image { Source = _shot, Width = W, Height = H, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        _root.Children.Add(img);
        _root.Children.Add(_ink);
        if (mode != OverlayMode.Pick)
        {
            _dim.Data = new RectangleGeometry(new Rect(0, 0, W, H));
            _root.Children.Add(_dim);
            _root.Children.Add(_selBorder);
            _root.Children.Add(_sizeTag);
            _root.Children.Add(_toolbar);
            _sizeTag.Visibility = Visibility.Collapsed;
            var hint = TagBox(mode == OverlayMode.Select ? "Выдели область  ·  Esc — отмена" : "Выдели область для скриншота  ·  Esc — отмена");
            Canvas.SetLeft(hint, W / 2 - 150); Canvas.SetTop(hint, 24);
            hint.Name = "hint";
            _root.Children.Add(hint);
        }
        else
        {
            var st = new StackPanel();
            RenderOptions.SetBitmapScalingMode(_magImg, BitmapScalingMode.NearestNeighbor);
            st.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(6, 6, 6, 0), Child = new Grid { Children = { _magImg, new Rectangle { Width = 9, Height = 9, Stroke = Brushes.White, StrokeThickness = 1.2 } } } });
            st.Children.Add(_magHex);
            _magnifier.Child = st;
            _root.Children.Add(_magnifier);
            Cursor = Cursors.None;
        }
        Content = _root;
        BuildToolbar();

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, b.Left, b.Top, b.W, b.H, Native.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) => { Activate(); Focus(); if (_mode == OverlayMode.Pick) UpdateMagnifier(Mouse.GetPosition(_root)); };
        Closed += (_, _) => _tcs.TrySetResult(null);
    }

    static Border TagBox(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 22)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5), IsHitTestVisible = false,
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12.5 },
    };

    // ---------------- мышь ----------------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_root);
        if (_mode == OverlayMode.Pick) { PickAt(p); return; }
        if (_editing)
        {
            if (!_sel.Contains(p)) return;
            if (_tool == Tool.Text) { AddText(p); e.Handled = true; return; }
            BeginShape(p);
            CaptureMouse();
            return;
        }
        _start = p; _dragging = true;
        CaptureMouse();
        foreach (UIElement c in _root.Children) if (c is FrameworkElement { Name: "hint" } fe) fe.Visibility = Visibility.Collapsed;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(_root);
        if (_mode == OverlayMode.Pick) { UpdateMagnifier(p); return; }
        if (_cur != null) { UpdateShape(p); return; }
        if (!_dragging) return;
        _sel = new Rect(_start, p);
        UpdateSelection();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_cur != null) { _undo.Add(_cur); _cur = null; return; }
        if (!_dragging) return;
        _dragging = false;
        if (_sel.Width < 4 || _sel.Height < 4)
        {
            // клик без выделения — весь экран
            _sel = new Rect(0, 0, ActualWidth, ActualHeight);
            UpdateSelection();
        }
        if (_mode == OverlayMode.Select) { Finish(Crop()); return; }
        _editing = true;
        ShowToolbar();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) { if (e.Key == Key.Escape) Keyboard.ClearFocus(); return; }
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) Undo();
        else if (_editing && (e.Key == Key.Enter || (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control))) Done();
    }

    void UpdateSelection()
    {
        double W = ActualWidth, H = ActualHeight;
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, W, H)), new RectangleGeometry(_sel));
        _selBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selBorder, _sel.X); Canvas.SetTop(_selBorder, _sel.Y);
        _selBorder.Width = _sel.Width; _selBorder.Height = _sel.Height;
        _sizeTag.Visibility = Visibility.Visible;
        ((TextBlock)_sizeTag.Child).Text = $"{Math.Round(_sel.Width * _scale)} × {Math.Round(_sel.Height * _scale)}";
        Canvas.SetLeft(_sizeTag, _sel.X); Canvas.SetTop(_sizeTag, Math.Max(0, _sel.Y - 30));
    }

    // ---------------- рисование ----------------
    Brush ColorBrush(byte a = 255) { var b = new SolidColorBrush(Color.FromArgb(a, _color.R, _color.G, _color.B)); b.Freeze(); return b; }

    void BeginShape(Point p)
    {
        _curStart = p;
        switch (_tool)
        {
            case Tool.Pen:
                _cur = new Polyline { Stroke = ColorBrush(), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = { p } };
                break;
            case Tool.Marker:
                _cur = new Polyline { Stroke = ColorBrush(110), StrokeThickness = 18, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = { p } };
                break;
            case Tool.Rect:
                _cur = new Rectangle { Stroke = ColorBrush(), StrokeThickness = 3, RadiusX = 4, RadiusY = 4 };
                Canvas.SetLeft(_cur, p.X); Canvas.SetTop(_cur, p.Y);
                break;
            case Tool.Arrow:
                _cur = new Path { Fill = ColorBrush(), Stroke = ColorBrush(), StrokeThickness = 3.5, StrokeLineJoin = PenLineJoin.Round, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round };
                break;
        }
        if (_cur != null) _ink.Children.Add(_cur);
    }

    void UpdateShape(Point p)
    {
        p = new Point(Math.Clamp(p.X, _sel.Left, _sel.Right), Math.Clamp(p.Y, _sel.Top, _sel.Bottom));
        switch (_cur)
        {
            case Polyline pl: pl.Points.Add(p); break;
            case Rectangle r:
                var rr = new Rect(_curStart, p);
                Canvas.SetLeft(r, rr.X); Canvas.SetTop(r, rr.Y); r.Width = rr.Width; r.Height = rr.Height;
                break;
            case Path path: path.Data = ArrowGeometry(_curStart, p); break;
        }
    }

    static Geometry ArrowGeometry(Point a, Point b)
    {
        var v = b - a;
        double len = v.Length;
        var g = new StreamGeometry();
        if (len < 1) return g;
        v.Normalize();
        double head = Math.Min(22, len * 0.45);
        var n = new Vector(-v.Y, v.X);
        var baseP = b - v * head;
        using (var c = g.Open())
        {
            c.BeginFigure(a, false, false); c.LineTo(baseP, true, true);
            c.BeginFigure(b, true, true);
            c.LineTo(baseP + n * head * 0.5, true, true);
            c.LineTo(baseP - n * head * 0.5, true, true);
        }
        g.Freeze();
        return g;
    }

    void AddText(Point p)
    {
        var tb = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ColorBrush(), CaretBrush = ColorBrush(),
            FontSize = 22, FontWeight = FontWeights.SemiBold, MinWidth = 40, AcceptsReturn = true,
        };
        Canvas.SetLeft(tb, p.X); Canvas.SetTop(tb, p.Y - 14);
        _ink.IsHitTestVisible = true;
        _ink.Children.Add(tb);
        tb.LostKeyboardFocus += (_, _) =>
        {
            int i = _ink.Children.IndexOf(tb);
            if (i < 0) return;
            _ink.Children.RemoveAt(i);
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            var t = new TextBlock { Text = tb.Text, Foreground = tb.Foreground, FontSize = 22, FontWeight = FontWeights.SemiBold,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 } };
            Canvas.SetLeft(t, Canvas.GetLeft(tb) + 3); Canvas.SetTop(t, Canvas.GetTop(tb) + 1);
            _ink.Children.Insert(i, t);
            _undo.Add(t);
            _ink.IsHitTestVisible = false;
        };
        Dispatcher.BeginInvoke(() => { tb.Focus(); Keyboard.Focus(tb); });
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _ink.Children.Remove(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    // ---------------- панель инструментов ----------------
    readonly Dictionary<Tool, Button> _toolButtons = new();
    readonly List<(Color c, Border dot)> _colorDots = new();

    void BuildToolbar()
    {
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)), CornerRadius = new CornerRadius(14), Padding = new Thickness(6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), BorderThickness = new Thickness(1),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        void AddTool(Tool t, string glyph, string tip)
        {
            var b = IconBtn(glyph, tip, () => { _tool = t; RefreshToolbar(); });
            _toolButtons[t] = b; sp.Children.Add(b);
        }
        AddTool(Tool.Arrow, "", "Стрелка");
        AddTool(Tool.Rect, "", "Рамка");
        AddTool(Tool.Pen, "", "Карандаш");
        AddTool(Tool.Marker, "", "Маркер");
        AddTool(Tool.Text, "", "Текст");
        sp.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 6, 6, 6), Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)) });
        foreach (var c in new[] { Color.FromRgb(255, 59, 48), Color.FromRgb(255, 204, 0), Color.FromRgb(52, 199, 89), Color.FromRgb(10, 132, 255), Colors.White, Colors.Black })
        {
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(c), Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            var cc = c;
            dot.MouseLeftButtonUp += (_, e) => { _color = cc; RefreshToolbar(); e.Handled = true; };
            dot.MouseLeftButtonDown += (_, e) => e.Handled = true;
            _colorDots.Add((c, dot)); sp.Children.Add(dot);
        }
        sp.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 6, 6, 6), Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)) });
        sp.Children.Add(IconBtn("", "Отменить (Ctrl+Z)", Undo));
        sp.Children.Add(IconBtn("", "Закрыть (Esc)", Close));
        var done = new Button
        {
            Content = "Готово", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand,
            Foreground = Brushes.Black, Background = Brushes.White, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            Template = RoundTemplate(9),
        };
        done.Click += (_, _) => Done();
        sp.Children.Add(done);
        bar.Child = sp;
        _toolbar.Children.Add(bar);
        _toolbar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _toolbar.MouseLeftButtonUp += (_, e) => e.Handled = true;
        _toolbar.Cursor = Cursors.Arrow;
        RefreshToolbar();
    }

    static ControlTemplate RoundTemplate(double r)
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(r));
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        return t;
    }

    static Button IconBtn(string glyph, string tip, Action a)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 },
            Width = 34, Height = 34, ToolTip = tip, Cursor = Cursors.Hand, Foreground = Brushes.White,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Template = RoundTemplate(9),
        };
        b.Click += (_, _) => a();
        return b;
    }

    void RefreshToolbar()
    {
        foreach (var (t, b) in _toolButtons)
            b.Background = t == _tool ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : Brushes.Transparent;
        foreach (var (c, dot) in _colorDots)
            dot.BorderBrush = c == _color ? Brushes.White : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
    }

    void ShowToolbar()
    {
        _toolbar.Visibility = Visibility.Visible;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = _toolbar.DesiredSize;
        double x = Math.Clamp(_sel.Right - sz.Width, 8, ActualWidth - sz.Width - 8);
        double y = _sel.Bottom + 10 + sz.Height < ActualHeight ? _sel.Bottom + 10 : Math.Max(8, _sel.Top - sz.Height - 10);
        if (_sel.Height > ActualHeight - 120) y = _sel.Bottom - sz.Height - 16;
        Canvas.SetLeft(_toolbar, x); Canvas.SetTop(_toolbar, y);
    }

    // ---------------- результат ----------------
    Int32Rect PixelRect()
    {
        int x = (int)Math.Round(_sel.X * _scale), y = (int)Math.Round(_sel.Y * _scale);
        int w = (int)Math.Round(_sel.Width * _scale), h = (int)Math.Round(_sel.Height * _scale);
        x = Math.Clamp(x, 0, _shot.PixelWidth - 1); y = Math.Clamp(y, 0, _shot.PixelHeight - 1);
        w = Math.Clamp(w, 1, _shot.PixelWidth - x); h = Math.Clamp(h, 1, _shot.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    BitmapSource Crop()
    {
        var c = new CroppedBitmap(_shot, PixelRect());
        c.Freeze();
        return c;
    }

    void Done()
    {
        Keyboard.ClearFocus();
        var pr = PixelRect();
        var crop = Crop();
        BitmapSource result = crop;
        if (_ink.Children.Count > 0)
        {
            double w = pr.Width / _scale, h = pr.Height / _scale;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(crop, new Rect(0, 0, w, h));
                var vb = new VisualBrush(_ink) { Viewbox = new Rect(pr.X / _scale, pr.Y / _scale, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                // VisualBrush берёт границы содержимого — фиксируем их невидимым прямоугольником на весь холст
                _ink.Children.Insert(0, new Rectangle { Width = ActualWidth, Height = ActualHeight, Fill = Brushes.Transparent });
                _ink.UpdateLayout();
                dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(pr.Width, pr.Height, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            result = rtb;
        }
        Finish(result);
    }

    void Finish(object? result)
    {
        _tcs.TrySetResult(result);
        Close();
    }

    // ---------------- пипетка ----------------
    Color PixelAt(Point p)
    {
        int x = Math.Clamp((int)(p.X * _scale), 0, _shot.PixelWidth - 1), y = Math.Clamp((int)(p.Y * _scale), 0, _shot.PixelHeight - 1);
        var px = new byte[4];
        _shot.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    void UpdateMagnifier(Point p)
    {
        int cx = (int)(p.X * _scale), cy = (int)(p.Y * _scale);
        int x = Math.Clamp(cx - 7, 0, _shot.PixelWidth - 15), y = Math.Clamp(cy - 7, 0, _shot.PixelHeight - 15);
        _magImg.Source = new CroppedBitmap(_shot, new Int32Rect(x, y, 15, 15));
        var c = PixelAt(p);
        _magHex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        double mx = p.X + 24, my = p.Y + 24;
        if (mx + 132 > ActualWidth) mx = p.X - 156;
        if (my + 154 > ActualHeight) my = p.Y - 178;
        Canvas.SetLeft(_magnifier, mx); Canvas.SetTop(_magnifier, my);
    }

    void PickAt(Point p)
    {
        var c = PixelAt(p);
        Finish(c);
    }
}
