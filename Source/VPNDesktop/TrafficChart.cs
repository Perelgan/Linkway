using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VPNDesktop;

public sealed class TrafficChart : Control
{
    private const int Capacity = 60;
    private readonly (double Download, double Upload)[] samples = new (double, double)[Capacity];
    private int count, cursor;

    internal void Add(double download, double upload)
    {
        samples[cursor] = (download, upload);
        cursor = (cursor + 1) % Capacity;
        count = Math.Min(count + 1, Capacity);
        InvalidateVisual();
    }

    internal void Clear()
    {
        count = cursor = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth <= 1 || ActualHeight <= 2)
            return;
        context.DrawLine(new Pen(Background, 1), new Point(0, ActualHeight - 1), new Point(ActualWidth, ActualHeight - 1));
        if (count < 2)
            return;

        double scale = 1024;
        for (int index = 0; index < count; index++)
        {
            var sample = samples[(cursor - count + index + Capacity) % Capacity];
            scale = Math.Max(scale, Math.Max(sample.Download, sample.Upload));
        }
        DrawSeries(false, new Pen(Foreground, 2));
        DrawSeries(true, new Pen(Foreground, 1) { DashStyle = DashStyles.Dash });

        void DrawSeries(bool upload, Pen pen)
        {
            var series = new StreamGeometry();
            using (var path = series.Open())
            {
                for (int index = 0; index < count; index++)
                {
                    var sample = samples[(cursor - count + index + Capacity) % Capacity];
                    var point = new Point(ActualWidth * (Capacity - count + index) / (Capacity - 1),
                        ActualHeight - 1 - (upload ? sample.Upload : sample.Download) / scale * (ActualHeight - 2));
                    if (index == 0)
                        path.BeginFigure(point, false, false);
                    else
                        path.LineTo(point, true, false);
                }
            }
            context.DrawGeometry(null, pen, series);
        }
    }
}
