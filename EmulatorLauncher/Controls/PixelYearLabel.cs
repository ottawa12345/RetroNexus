using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace EmulatorLauncher.Controls;

/// <summary>年代ラベルをドット絵(5×7)で描くコントロール。</summary>
public sealed class PixelYearLabel : Grid
{
    // 光(にじみ)の外側への広がり(ドット単位ではなくpx)と不透明度。
    private const double Halo2Spread = 3.0;
    private const double Halo1Spread = 1.5;

    /// <summary>光が切れないよう、描画領域の外側に確保する余白(px)。</summary>
    public const double Pad = 4;

    private static readonly string[][] Glyphs =
    {
        new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" }, // 0
        new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" }, // 1
        new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" }, // 2
        new[] { "11110", "00001", "00001", "01110", "00001", "00001", "11110" }, // 3
        new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" }, // 4
        new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" }, // 5
        new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" }, // 6
        new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" }, // 7
        new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" }, // 8
        new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" }, // 9
    };

    private static readonly string[] Tilde = { "00000", "00000", "01000", "10101", "00010", "00000", "00000" };

    private readonly Microsoft.UI.Xaml.Shapes.Path _halo2 = new() { Opacity = 0.14 };
    private readonly Microsoft.UI.Xaml.Shapes.Path _halo1 = new() { Opacity = 0.30 };
    private readonly Microsoft.UI.Xaml.Shapes.Path _core = new();
    private readonly TextBlock _fallback = new()
    {
        FontWeight = FontWeights.SemiBold,
        TextAlignment = TextAlignment.Center,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private string? _text;
    private double _unit;
    private Color _color;

    /// <summary>描いた内容の高さ(余白を除く)。</summary>
    public double ContentHeight { get; private set; }

    public PixelYearLabel()
    {
        IsHitTestVisible = false;
        Children.Add(_halo2);
        Children.Add(_halo1);
        Children.Add(_core);
        Children.Add(_fallback);
    }

    /// <summary>ラベルを設定する。</summary>
    public void SetLabel(string text, double unit, Color color)
    {
        if (_text == text && _unit == unit && _color.Equals(color)) return;
        _text = text;
        _unit = unit;
        _color = color;

        var brush = new SolidColorBrush(color);

        if (!IsPixelText(text))
        {
            _halo2.Visibility = _halo1.Visibility = _core.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
            _fallback.Text = text;
            // 「年不明」(3文字)が、4桁の年(23マス)とほぼ同じ幅になる大きさ。
            _fallback.FontSize = unit * 7;
            _fallback.Width = unit * 23;
            _fallback.HorizontalAlignment = HorizontalAlignment.Left;
            _fallback.Foreground = brush;
            _fallback.Margin = new Thickness(Pad);
            ContentHeight = _fallback.FontSize * 1.4;
            Width = Pad * 2 + unit * 23;
            Height = ContentHeight + Pad * 2;
            return;
        }

        _fallback.Visibility = Visibility.Collapsed;
        _halo2.Visibility = _halo1.Visibility = _core.Visibility = Visibility.Visible;

        // 行ごとの文字列(1行 または 上の年・〜・下の年)
        double width;
        double height;
        var core = new GeometryGroup { FillRule = FillRule.Nonzero };
        var halo1 = new GeometryGroup { FillRule = FillRule.Nonzero };
        var halo2 = new GeometryGroup { FillRule = FillRule.Nonzero };

        int tilde = text.IndexOf('~');
        if (tilde < 0)
        {
            AddNumber(text, 0, 0, unit, core, halo1, halo2);
            width = unit * (text.Length * 6 - 1);
            height = unit * 7;
        }
        else
        {
            string first = text.Substring(0, tilde);
            string second = text.Substring(tilde + 1);
            int cols = Math.Max(first.Length, second.Length) * 6 - 1;
            AddNumber(first, 0, 0, unit, core, halo1, halo2);
            // 「〜」は幅の中央。
            AddGlyph(Tilde, (cols - 5) / 2.0 * unit, 6 * unit, unit, core, halo1, halo2);
            AddNumber(second, 0, 12 * unit, unit, core, halo1, halo2);
            width = unit * cols;
            height = unit * 19;
        }

        _core.Data = core;
        _halo1.Data = halo1;
        _halo2.Data = halo2;
        _core.Fill = brush;
        _halo1.Fill = brush;
        _halo2.Fill = brush;

        // 余白ぶんだけ内側へずらして、光が切れないようにする。
        var shift = new TranslateTransform { X = Pad, Y = Pad };
        _core.RenderTransform = shift;
        _halo1.RenderTransform = shift;
        _halo2.RenderTransform = shift;

        ContentHeight = height;
        Width = width + Pad * 2;
        Height = height + Pad * 2;
    }

    private static bool IsPixelText(string text)
    {
        if (text.Length == 0) return false;
        foreach (char c in text)
        {
            if (!(c >= '0' && c <= '9') && c != '~') return false;
        }
        return true;
    }

    private static void AddNumber(string digits, double x, double y, double unit,
        GeometryGroup core, GeometryGroup halo1, GeometryGroup halo2)
    {
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[i] - '0';
            if (d < 0 || d > 9) continue;
            AddGlyph(Glyphs[d], x + i * 6 * unit, y, unit, core, halo1, halo2);
        }
    }

    /// <summary>1文字ぶんを、同じ行で連続するドットを1つの四角にまとめて追加する。</summary>
    private static void AddGlyph(string[] glyph, double x, double y, double unit,
        GeometryGroup core, GeometryGroup halo1, GeometryGroup halo2)
    {
        for (int r = 0; r < glyph.Length; r++)
        {
            string row = glyph[r];
            int c = 0;
            while (c < row.Length)
            {
                if (row[c] != '1') { c++; continue; }
                int start = c;
                while (c < row.Length && row[c] == '1') c++;
                double rx = x + start * unit;
                double ry = y + r * unit;
                double rw = (c - start) * unit;
                core.Children.Add(MakeRect(rx, ry, rw, unit));
                halo1.Children.Add(MakeRect(rx - Halo1Spread, ry - Halo1Spread, rw + Halo1Spread * 2, unit + Halo1Spread * 2));
                halo2.Children.Add(MakeRect(rx - Halo2Spread, ry - Halo2Spread, rw + Halo2Spread * 2, unit + Halo2Spread * 2));
            }
        }
    }

    private static RectangleGeometry MakeRect(double x, double y, double w, double h)
        => new() { Rect = new Rect(x, y, w, h) };
}
