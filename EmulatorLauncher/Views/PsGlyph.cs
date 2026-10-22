using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace EmulatorLauncher.Views;

/// <summary>PSのフェイスボタンの記号(✕○△□)を、フォントの字形に頼らず図形で描く。</summary>
internal static class PsGlyph
{
    /// <summary>文字が✕○△□のどれかなら、'X'(✕) 'C'(○) 'S'(□) 'T'(△)を返す。</summary>
    public static bool TryGetKind(string? text, out char kind)
    {
        kind = '\0';
        switch (text)
        {
            case "✕": case "×": case "✖": kind = 'X'; return true;
            case "○": case "◯": case "〇": kind = 'C'; return true;
            case "□": kind = 'S'; return true;
            case "△": case "▲": kind = 'T'; return true;
            default: return false;
        }
    }

    /// <summary>中心(cx,cy)、半分の大きさhで記号を作る。</summary>
    public static List<Shape> Create(char kind, double cx, double cy, double h, Brush stroke, double thickness)
    {
        var list = new List<Shape>();
        void Add(Shape sh)
        {
            sh.StrokeThickness = thickness; sh.Stroke = stroke; sh.IsHitTestVisible = false;
            list.Add(sh);
        }

        switch (kind)
        {
            case 'X':
                Add(new Line { X1 = cx - h, Y1 = cy - h, X2 = cx + h, Y2 = cy + h, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                Add(new Line { X1 = cx + h, Y1 = cy - h, X2 = cx - h, Y2 = cy + h, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                break;
            case 'C':
            {
                var c = new Ellipse { Width = h * 2.3, Height = h * 2.3 };
                Canvas.SetLeft(c, cx - h * 1.15); Canvas.SetTop(c, cy - h * 1.15);
                Add(c);
                break;
            }
            case 'S':
            {
                var q = new Rectangle { Width = h * 2.1, Height = h * 2.1 };
                Canvas.SetLeft(q, cx - h * 1.05); Canvas.SetTop(q, cy - h * 1.05);
                Add(q);
                break;
            }
            default:
                Add(new Polygon
                {
                    Points = new PointCollection
                    {
                        new Windows.Foundation.Point(cx, cy - h * 1.2),
                        new Windows.Foundation.Point(cx + h * 1.25, cy + h * 0.9),
                        new Windows.Foundation.Point(cx - h * 1.25, cy + h * 0.9),
                    },
                    StrokeLineJoin = PenLineJoin.Round,
                });
                break;
        }
        return list;
    }
}
