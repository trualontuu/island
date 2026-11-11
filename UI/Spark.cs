using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Island.UI;

/// <summary>Мини-график за последнюю минуту: заливка с градиентом + линия. Рисуется раз в секунду.</summary>
public sealed class Spark : FrameworkElement
{
    public IReadOnlyCollection<double>? Values, Values2;
    /// <summary>Шкала: 0 — автоматически по максимуму.</summary>
    public double Max = 100;
    public Color Color = Colors.DeepSkyBlue;
    public Color Color2 = Colors.Orange;

    static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1));
    static Pen Freeze(Pen p) { p.Freeze(); return p; }

    public Spark() { IsHitTestVisible = false; }

    public void Update() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        // сетка
        var grid = GridPen;
        for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(0, Math.Round(h * i / 4) + 0.5), new Point(w, Math.Round(h * i / 4) + 0.5));

        double max = Max;
        if (max <= 0)
        {
            double m = 0;
            if (Values != null && Values.Count > 0) m = Math.Max(m, Values.Max());
            if (Values2 != null && Values2.Count > 0) m = Math.Max(m, Values2.Max());
            max = Math.Max(m * 1.15, 1);
        }
        if (Values2 != null) Draw(dc, Values2, Color2, max, w, h, false);
        if (Values != null) Draw(dc, Values, Color, max, w, h, true);
    }

    static void Draw(DrawingContext dc, IReadOnlyCollection<double> vals, Color c, double max, double w, double h, bool fill)
    {
        if (vals.Count < 2) return;
        double step = w / (SystemMonitorHistory - 1);
        double x0 = w - (vals.Count - 1) * step;
        var pts = new List<Point>(vals.Count);
        int i = 0;
        foreach (var v in vals) pts.Add(new Point(x0 + i++ * step, h - 1 - Math.Clamp(v / max, 0, 1) * (h - 3)));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(pts[0], false, false);
            // сглаживание: кривые через середины отрезков
            for (int k = 1; k < pts.Count; k++)
            {
                var mid = new Point((pts[k - 1].X + pts[k].X) / 2, (pts[k - 1].Y + pts[k].Y) / 2);
                g.QuadraticBezierTo(pts[k - 1], mid, true, true);
            }
            g.LineTo(pts[^1], true, true);
        }
        line.Freeze();

        if (fill)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(pts[0].X, h), true, true);
                g.LineTo(pts[0], false, false);
                for (int k = 1; k < pts.Count; k++)
                {
                    var mid = new Point((pts[k - 1].X + pts[k].X) / 2, (pts[k - 1].Y + pts[k].Y) / 2);
                    g.QuadraticBezierTo(pts[k - 1], mid, false, true);
                }
                g.LineTo(pts[^1], false, false);
                g.LineTo(new Point(pts[^1].X, h), false, false);
            }
            area.Freeze();
            var br = new LinearGradientBrush(Color.FromArgb(90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            br.Freeze();
            dc.DrawGeometry(br, null, area);
        }
        var pen = new Pen(new SolidColorBrush(fill ? c : Color.FromArgb(200, c.R, c.G, c.B)), fill ? 1.8 : 1.3) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    const int SystemMonitorHistory = Island.Services.SystemMonitor.History;
}
