using System;
using System.Collections.Generic;
using System.Linq;
using EmulatorLauncher.Input;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace EmulatorLauncher.Views;

/// <summary>
/// 設定中のコンソールのコントローラー(参照用)。ConsoleControllerLayoutsの定義から、コードで小さく描く(画像ファイルは使わない)。
/// 左上のコントローラー図で選んだボタンの割り当て先を、ここでも光らせる。
/// </summary>
public sealed class ConsolePadView : Canvas
{
    public const double Scale = 0.5;
    public const double ViewWidth = ControllerLayout.CanvasWidth * Scale;
    public const double ViewHeight = ControllerLayout.CanvasHeight * Scale;

    private const double PulseSeconds = 1.3;
    private static readonly Color PulseLight = Color.FromArgb(255, 0x7C, 0x92, 0xFF);

    private sealed class Piece
    {
        public List<Shape> Shapes = new();
        public List<Color> BaseColors = new();
        public TextBlock? Label;
    }

    private readonly bool _dark;   // 黒い背景用(ゲーム中の「操作方法の確認」)の配色か
    private readonly Dictionary<string, Piece> _pieces = new();
    private Storyboard? _pulse;
    private HashSet<string> _litNow = new();

    public ConsolePadView(ControllerLayout layout, bool dark = false)
    {
        _dark = dark;
        Width = ViewWidth;
        Height = ViewHeight;

        foreach (PadBodyPart part in layout.Body)
        {
            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = part.W * Scale,
                Height = part.H * Scale,
                RadiusX = part.Radius * Scale,
                RadiusY = part.Radius * Scale,
                Fill = new SolidColorBrush(part.IsScreen ? Color.FromArgb(255, 0x2B, 0x2B, 0x2B) : (_dark ? Color.FromArgb(255, 0x14, 0x14, 0x14) : Colors.White)),
                Stroke = new SolidColorBrush(_dark ? Color.FromArgb(255, 0x9A, 0x9A, 0x9A) : Color.FromArgb(255, 0x8A, 0x8A, 0x8A)),
                StrokeThickness = 2,
            };
            SetLeft(r, part.X * Scale);
            SetTop(r, part.Y * Scale);
            Children.Add(r);
        }

        var cButtons = new List<(PadElement E, Piece P)>();
        var chips = new List<(Border Chip, double Cx, double Cy, double W, double H)>();   // 収まらない名前の札
        foreach (PadElement e in layout.Elements)
        {
            // 参照図は「実機にあるボタンの種類と配置」を見せるためのもの。実機に無い操作(振る、息を吹く、タッチ等)は描かない。
            if (ConsoleControllerLayouts.IsVirtualAction(e.Label)) continue;

            Color color = ParseColor(e.Color);
            var piece = new Piece();
            double x = e.X * Scale, y = e.Y * Scale, w = e.W * Scale, h = e.H * Scale;

            if (e.Shape == PadShape.DPad)
            {
                double arm = Math.Max(w * 0.34, 6);
                piece.Shapes.Add(MakeRect(x + (w - arm) / 2, y, arm, h, 3, color));
                piece.Shapes.Add(MakeRect(x, y + (h - arm) / 2, w, arm, 3, color));
            }
            else if (e.Shape == PadShape.Pill)
            {
                piece.Shapes.Add(MakeRect(x, y, w, h, h / 2, color));
            }
            else if (e.Shape == PadShape.Stick)
            {
                // スティックは二重線(外の輪 + 内側の丸)。光るのは内側の丸。
                var ring = new Ellipse { Width = w, Height = h, Fill = new SolidColorBrush(_dark ? Color.FromArgb(255, 0x14, 0x14, 0x14) : Colors.White), Stroke = new SolidColorBrush(color), StrokeThickness = 2.5 };
                SetLeft(ring, x); SetTop(ring, y);
                Children.Add(ring);

                double iw = w * 0.62, ih = h * 0.62;
                var inner = new Ellipse { Width = iw, Height = ih, Fill = new SolidColorBrush(color), Stroke = Outline(), StrokeThickness = _dark ? 1.5 : 0 };
                SetLeft(inner, x + (w - iw) / 2); SetTop(inner, y + (h - ih) / 2);
                Children.Add(inner);
                piece.Shapes.Add(inner);
            }
            else
            {
                var el = new Ellipse { Width = w, Height = h, Fill = new SolidColorBrush(color), Stroke = Outline(), StrokeThickness = _dark ? 1.5 : 0 };
                SetLeft(el, x); SetTop(el, y);
                Children.Add(el);
                piece.Shapes.Add(el);
            }

            foreach (Shape s in piece.Shapes) piece.BaseColors.Add(color);

            if (e.Shape == PadShape.Round && PsGlyph.TryGetKind(e.Label, out char psKind))
            {
                // PSの記号(✕○△□)は文字ではなく図形で、どの大きさのボタンでもそろえて描く
                var white = new SolidColorBrush(Colors.White);
                foreach (Shape g in PsGlyph.Create(psKind, x + w / 2, y + h / 2, Math.Min(w, h) * 0.23, white, 2.2))
                {
                    Children.Add(g);
                }
            }
            else if (!string.IsNullOrEmpty(e.Label) && e.Shape != PadShape.DPad)
            {
                double font = Math.Clamp(h * 0.42, 10, 16);

                // ボタンに収まらない長い名前は、切れないように白い札に載せて、全部表示する(札同士が重なる場合は後でずらす)。
                bool fits = EstimateTextWidth(e.Label, font) <= w * 0.9;
                if (!fits)
                {
                    double cf = Math.Min(font, 10);
                    double cw = EstimateTextWidth(e.Label, cf) + 10, ch = cf * 1.35 + 3;
                    var chipText = new TextBlock
                    {
                        Text = e.Label, FontSize = cf, FontFamily = PadDiagram.SystemFont, FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x1E)),
                        TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    };
                    var chip = new Border
                    {
                        Width = cw, Height = ch, CornerRadius = new CornerRadius(ch / 2),
                        Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                        BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1.5),
                        Child = chipText, IsHitTestVisible = false,
                    };
                    chips.Add((chip, x + w / 2, y + h / 2, cw, ch));
                }
                else
                {
                var tb = new TextBlock
                {
                    Text = e.Label,
                    FontSize = font,
                    FontFamily = PadDiagram.SystemFont,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(fits ? Colors.White : Color.FromArgb(255, 0x1E, 0x1E, 0x1E)),
                    TextAlignment = TextAlignment.Center,
                    Width = Math.Max(w, 40),
                    IsHitTestVisible = false,
                };
                SetLeft(tb, x + w / 2 - tb.Width / 2);
                SetTop(tb, y + (h - font * 1.35) / 2);
                Children.Add(tb);
                piece.Label = tb;
                }
            }

            if (e.Key is not null) _pieces[e.Key] = piece;
            else if (e.Shape == PadShape.Round && e.Label == "C") cButtons.Add((e, piece));
            else if (e.Shape == PadShape.Stick && e.Label == "C")
            {
                // Cスティック(ゲームキューブ): 右スティックの4方向すべてで、このスティックを光らせる
                foreach (string dir in new[] { "RSUp", "RSDown", "RSLeft", "RSRight" }) _pieces[dir] = piece;
            }
        }

        PlaceChips(chips);

        // N64のCボタン(キーなし)は、右スティックの4方向の割り当て(RSUp等)に、図の中の位置で対応づける
        if (cButtons.Count == 4)
        {
            _pieces["RSUp"] = cButtons.OrderBy(c => c.E.Y).First().P;
            _pieces["RSDown"] = cButtons.OrderByDescending(c => c.E.Y).First().P;
            _pieces["RSLeft"] = cButtons.OrderBy(c => c.E.X).First().P;
            _pieces["RSRight"] = cButtons.OrderByDescending(c => c.E.X).First().P;
        }
    }

    /// <summary>収まらない名前の札を、ボタンの中心に置く。札同士が重なるときは、下へずらして全部読めるようにする。</summary>
    private void PlaceChips(List<(Border Chip, double Cx, double Cy, double W, double H)> chips)
    {
        var placed = new List<(double L, double T, double R, double B)>();
        foreach (var c in chips.OrderBy(c => c.Cy).ThenBy(c => c.Cx))
        {
            double left = Math.Clamp(c.Cx - c.W / 2, 0, ViewWidth - c.W);
            double top = c.Cy - c.H / 2;
            bool moved;
            int guard = 0;
            do
            {
                moved = false;
                foreach (var p in placed)
                {
                    if (left < p.R && left + c.W > p.L && top < p.B && top + c.H > p.T)
                    {
                        top = p.B + 1;
                        moved = true;
                    }
                }
            } while (moved && ++guard < 20);
            top = Math.Clamp(top, 0, ViewHeight - c.H);
            SetLeft(c.Chip, left); SetTop(c.Chip, top);
            Children.Add(c.Chip);
            placed.Add((left, top, left + c.W, top + c.H));
        }
    }

    /// <summary>文字の幅のおおよその見積り(全角は1em、半角は0.6em)。</summary>
    private static double EstimateTextWidth(string text, double font)
    {
        double em = 0;
        foreach (char c in text) em += c > 0x7F ? 1.0 : 0.6;
        return em * font;
    }

    /// <summary>黒い背景では、暗い色のボタンが本体に埋もれないよう、うすい灰色の縁を付ける(明るい背景では付けない)。</summary>
    private Brush? Outline() => _dark ? new SolidColorBrush(Color.FromArgb(255, 0x9A, 0x9A, 0x9A)) : null;

    private Microsoft.UI.Xaml.Shapes.Rectangle MakeRect(double x, double y, double w, double h, double radius, Color color)
    {
        var r = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = w, Height = h, RadiusX = radius, RadiusY = radius, Fill = new SolidColorBrush(color),
            Stroke = Outline(), StrokeThickness = _dark ? 1.5 : 0,
        };
        SetLeft(r, x); SetTop(r, y);
        Children.Add(r);
        return r;
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            string h = hex.TrimStart('#');
            return Color.FromArgb(255, Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16));
        }
        catch (Exception)
        {
            return Color.FromArgb(255, 0x3B, 0x3B, 0x3B);
        }
    }

    /// <summary>litKeysのボタン(PadElementのKey)をゆっくり光らせる。組が変わらなければ何もしない。</summary>
    public void Update(ICollection<string> litKeys)
    {
        var lit = new HashSet<string>(litKeys.Where(_pieces.ContainsKey));
        if (lit.SetEquals(_litNow)) return;

        _pulse?.Stop();
        _pulse = null;

        // 元の色に戻す
        foreach (string key in _litNow)
        {
            if (!_pieces.TryGetValue(key, out Piece? old)) continue;
            for (int i = 0; i < old.Shapes.Count; i++) old.Shapes[i].Fill = new SolidColorBrush(old.BaseColors[i]);
        }

        _litNow = lit;
        if (lit.Count == 0) return;

        var sb = new Storyboard();
        foreach (string key in lit)
        {
            Piece p = _pieces[key];
            foreach (Shape s in p.Shapes)
            {
                var brush = new SolidColorBrush(Color.FromArgb(255, 0x30, 0x50, 0xEF));
                s.Fill = brush;
                var anim = new ColorAnimation
                {
                    From = Color.FromArgb(255, 0x30, 0x50, 0xEF),
                    To = PulseLight,
                    Duration = new Duration(TimeSpan.FromSeconds(PulseSeconds)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    EnableDependentAnimation = true,
                };
                Storyboard.SetTarget(anim, brush);
                Storyboard.SetTargetProperty(anim, "Color");
                sb.Children.Add(anim);
            }
        }

        _pulse = sb;
        sb.Begin();
    }
}
