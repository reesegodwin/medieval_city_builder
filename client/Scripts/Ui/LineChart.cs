using Godot;

namespace MedievalClient.Ui;

/// <summary>
/// A small line chart: one or more series over the same days, a zero line, the top and bottom values and a
/// legend. Values are plotted oldest on the left, newest on the right.
/// </summary>
public partial class LineChart : Control
{
    public sealed record Series(string Label, Color Color, double[] Values);

    private static readonly Color Background = new(0f, 0f, 0f, 0.35f);
    private static readonly Color Grid = new(1f, 1f, 1f, 0.15f);
    private static readonly Color Text = new(0.85f, 0.85f, 0.8f);
    private const int FontSize = 13;
    private const float Pad = 6;

    private IReadOnlyList<Series> _series = Array.Empty<Series>();
    private string _title = "";

    public LineChart()
    {
        CustomMinimumSize = new Vector2(0, 120);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void SetData(string title, IReadOnlyList<Series> series)
    {
        _title = title;
        _series = series;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Background);

        var plot = new Rect2(Pad + 44, Pad + 28, size.X - Pad * 2 - 44, size.Y - Pad * 2 - 44);
        var points = _series.Where(s => s.Values.Length > 0).ToList();
        if (points.Count == 0 || plot.Size.X < 10 || plot.Size.Y < 10)
        {
            DrawString(font, new Vector2(Pad, Pad + FontSize), $"{_title}: not enough days in the books yet", fontSize: FontSize, modulate: Text);
            return;
        }

        double min = Math.Min(0, points.Min(s => s.Values.Min()));
        double max = Math.Max(0, points.Max(s => s.Values.Max()));
        if (max - min < 1e-6) max = min + 1;
        int days = points.Max(s => s.Values.Length);
        float X(int i) => plot.Position.X + (days <= 1 ? plot.Size.X : plot.Size.X * i / (days - 1));
        float Y(double v) => plot.Position.Y + (float)((max - v) / (max - min)) * plot.Size.Y;

        // Frame, zero line and value labels.
        DrawLine(new Vector2(plot.Position.X, plot.Position.Y), new Vector2(plot.End.X, plot.Position.Y), Grid);
        DrawLine(new Vector2(plot.Position.X, plot.End.Y), new Vector2(plot.End.X, plot.End.Y), Grid);
        if (min < 0) DrawLine(new Vector2(plot.Position.X, Y(0)), new Vector2(plot.End.X, Y(0)), Grid with { A = 0.4f }, 1.5f);
        DrawString(font, new Vector2(Pad, plot.Position.Y + 4), Short(max), fontSize: FontSize - 1, modulate: Text);
        DrawString(font, new Vector2(Pad, plot.End.Y + 4), Short(min), fontSize: FontSize - 1, modulate: Text);
        DrawString(font, new Vector2(plot.Position.X, size.Y - Pad), $"{days} days ago", fontSize: FontSize - 1, modulate: Text);
        DrawString(font, new Vector2(plot.End.X - 70, size.Y - Pad), "yesterday", HorizontalAlignment.Right, 70, FontSize - 1, Text);

        foreach (var s in points)
        {
            var line = new Vector2[s.Values.Length];
            int offset = days - s.Values.Length;
            for (int i = 0; i < s.Values.Length; i++) line[i] = new Vector2(X(i + offset), Y(s.Values[i]));
            if (line.Length == 1) DrawCircle(line[0], 2.5f, s.Color);
            else DrawPolyline(line, s.Color, 2f, antialiased: true);
        }

        // Title and legend along the top.
        float x = Pad;
        DrawString(font, new Vector2(x, Pad + FontSize), _title, fontSize: FontSize, modulate: Text);
        x += font.GetStringSize(_title, fontSize: FontSize).X + 14;
        foreach (var s in points)
        {
            DrawRect(new Rect2(x, Pad + 4, 10, 10), s.Color);
            DrawString(font, new Vector2(x + 14, Pad + FontSize), s.Label, fontSize: FontSize, modulate: Text);
            x += font.GetStringSize(s.Label, fontSize: FontSize).X + 28;
        }
    }

    private static string Short(double v) =>
        Math.Abs(v) >= 10000 ? $"{v / 1000:0}k" : Math.Abs(v) >= 1000 ? $"{v / 1000:0.#}k" : Math.Abs(v) >= 10 ? $"{v:0}" : $"{v:0.#}";
}
