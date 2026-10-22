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
/// 今使っているコントローラーの図(コードで描いた汎用の形。画像ファイルは使わない)。
/// 各ボタンに、そのコントローラーでの名前(PSなら ✕○□△ など)を重ね、割り当て先のコンソールのボタン名は
/// ボタンの外側に「札(角丸のバッジ)」で添える。右の一覧で選んでいる行に関係するボタンは、青い輪が光って示す。
///
/// 見た目の決まり:
///   ・札はどのボタンでも同じ形・同じ大きさの文字。ボタンの外側(上/下/左/右)へ同じ距離で置く。肩のボタンだけは細長い枠の内側の右。
///   ・割り当てが無いボタンには札を出さない。
///   ・選択中のボタンは塗りつぶさず、青い輪とやわらかい光をゆっくり脈打たせる(札は青の塗り+白文字に反転)。
/// </summary>
public sealed class PadDiagram : Canvas
{
    public const double DiagramWidth = 600;
    public const double DiagramHeight = 285;

    private sealed class Part
    {
        public Shape Shape = null!;
        public TextBlock? Name;       // コントローラー上のボタン名(ボタンの中)
        public TextBlock Assigned = null!;   // 割り当て先のコンソールのボタン名(札の中の文字)
        public Border Badge = null!;         // 札(割り当てが無いときは隠す)
        public Canvas BadgeGlyph = null!;    // 札の中のPS記号(✕○△□)の置き場。記号のときだけ文字の代わりに図形を描く
        public bool IsPill;
        public List<Shape> Glyph = new();   // PSのフェイスボタンの記号(✕○△□)は、文字ではなく図形で描く
        public Ellipse? Ring;               // 丸いボタンの、光る輪(選択中だけ見える)
        public Ellipse? Halo;               // その外側の、やわらかい光
    }

    /// <summary>コントローラーの図(と参照図)で使う、システム標準のフォント(日本語はYu Gothic UI等に切り替わる)。</summary>
    public static readonly FontFamily SystemFont = new("Segoe UI, Yu Gothic UI, Meiryo UI");

    private readonly Dictionary<GamepadButton, Part> _parts = new();

    /// <summary>図に描かれているボタン(「ボタン確認」で押下状態を読む対象)。</summary>
    public IEnumerable<GamepadButton> Buttons => _parts.Keys;

    /// <summary>右スティックの4方向。倒すと図の右スティック(R3)が光る。</summary>
    public static readonly GamepadButton[] StickDirections =
    {
        GamepadButton.RightStickUp, GamepadButton.RightStickDown, GamepadButton.RightStickLeft, GamepadButton.RightStickRight,
    };
    // 色。明るい背景用(設定画面)と、黒い背景用(ゲーム中の「操作方法の確認」)の2通り。コンストラクターで決める。
    private Brush _fill = new SolidColorBrush(Color.FromArgb(255, 0xF7, 0xF7, 0xF7));
    private Brush _line = new SolidColorBrush(Color.FromArgb(255, 0x8A, 0x8A, 0x8A));
    private Brush _text = new SolidColorBrush(Color.FromArgb(255, 0x2D, 0x2D, 0x2D));
    private Brush _blue = new SolidColorBrush(Color.FromArgb(255, 0x30, 0x50, 0xEF));
    private Brush _badgeBack = new SolidColorBrush(Color.FromArgb(255, 0xDC, 0xE6, 0xFF));
    private Brush _badgeText = new SolidColorBrush(Color.FromArgb(255, 0x30, 0x50, 0xEF));   // 札の中の文字(明るい背景では青)
    private Brush _bodyFill = new SolidColorBrush(Colors.White);
    private Brush _fixedFill = new SolidColorBrush(Color.FromArgb(255, 0x7A, 0x7A, 0x7A));
    private readonly Brush _white = new SolidColorBrush(Colors.White);
    private Color _pillLight = Color.FromArgb(255, 0xEA, 0xF0, 0xFF);
    private Color _pillDark = Color.FromArgb(255, 0xC9, 0xD6, 0xFF);

    // 光っているボタンは、青い輪が濃くなったり薄くなったりをゆっくり繰り返す(周期は片道PulseSeconds秒)。
    private const double PulseSeconds = 1.3;
    private Storyboard? _pulse;
    private HashSet<GamepadButton> _litNow = new();

    private const double BadgeHeight = 24;

    public PadDiagram(ControllerGlyphs.PadStyle style, bool dark = false)
    {
        if (dark)
        {
            _fill = new SolidColorBrush(Color.FromArgb(255, 0x2B, 0x2B, 0x2B));
            _line = new SolidColorBrush(Color.FromArgb(255, 0x9A, 0x9A, 0x9A));
            _text = new SolidColorBrush(Color.FromArgb(255, 0xF2, 0xF2, 0xF2));
            _blue = new SolidColorBrush(Color.FromArgb(255, 0x6C, 0x86, 0xFF));
            _badgeBack = new SolidColorBrush(Color.FromArgb(255, 0x26, 0x30, 0x5F));
            _badgeText = new SolidColorBrush(Color.FromArgb(255, 0xCF, 0xDA, 0xFF));
            _bodyFill = new SolidColorBrush(Color.FromArgb(255, 0x14, 0x14, 0x14));
            _fixedFill = new SolidColorBrush(Color.FromArgb(255, 0x8A, 0x8A, 0x8A));
            _pillLight = Color.FromArgb(255, 0x26, 0x35, 0x6E);
            _pillDark = Color.FromArgb(255, 0x3A, 0x4F, 0x9E);
        }

        Width = DiagramWidth;
        Height = DiagramHeight;

        // 本体(下のコンソールのコントローラー図と同じ、角丸の長方形)
        var body = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 544, Height = 190, RadiusX = 24, RadiusY = 24,
            Fill = _bodyFill,
            Stroke = _line,
            StrokeThickness = 3,
        };
        SetLeft(body, 28); SetTop(body, 85);
        Children.Add(body);

        // 十字キー(割り当ては変えられないので、位置の目安だけ)
        // (下のコンソール図の十字キーと同じ作り: 灰色の十字)
        // 本体の縦の中央に置き、左に寄せる
        AddFixedRect(88 - 10, 180 - 30, 20, 60);
        AddFixedRect(88 - 30, 180 - 10, 60, 20);

        // 肩のボタン(ボタン名とコンソール側の名前を並べて出す)
        AddPill(GamepadButton.LeftTrigger, style, 20, 8, 180, 26);
        AddPill(GamepadButton.LeftShoulder, style, 20, 40, 180, 26);
        AddPill(GamepadButton.RightTrigger, style, 400, 8, 180, 26);
        AddPill(GamepadButton.RightShoulder, style, 400, 40, 180, 26);

        // スティック(押し込み L3/R3。右スティックの方向の割り当ても、右スティックを光らせて示す)
        // 左は「CREATE」の真下、右は「OPTIONS」の真下に置く(フェイスボタンの左の札と重ならないよう、少し小さめ)
        AddRound(GamepadButton.LeftThumbstickButton, style, 195, 217, 26, "L3", side: 'B');
        AddRound(GamepadButton.RightThumbstickButton, style, 305, 217, 26, "R3", side: 'B');

        // フェイスボタン(押す位置は物理配置: A=下、B=右、X=左、Y=上)。札はそれぞれ外側へ。
        // 縦は、上の札(A連射など)と本体の上枠の隙間 = 下の札(B)と下枠の隙間 になる位置(札の上端89→91、下端267→269)。
        // 横は、ボタン1/3個ぶん(約13px)右へ。右の札が本体の枠からはみ出てもよい。
        AddRound(GamepadButton.Y, style, 458, 140, 19, null, side: 'T');
        AddRound(GamepadButton.A, style, 458, 220, 19, null, side: 'B');
        AddRound(GamepadButton.X, style, 418, 180, 19, null, side: 'L');
        AddRound(GamepadButton.B, style, 498, 180, 19, null, side: 'R');

        // 小さいボタン(VIEW/MENU)
        AddSmall(GamepadButton.View, style, 195, 132);
        AddSmall(GamepadButton.Menu, style, 305, 132);
    }

    private void AddFixedRect(double x, double y, double w, double h)
    {
        var r = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = w, Height = h, RadiusX = 3, RadiusY = 3, Fill = _fixedFill,
        };
        SetLeft(r, x); SetTop(r, y);
        Children.Add(r);
    }

    private TextBlock MakeText(string text, double size, Brush brush, bool bold)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = SystemFont,   // 丸ゴシックだと小さい文字がつぶれるので、この図だけシステム標準のフォント
            Foreground = brush,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
            IsTextScaleFactorEnabled = false,   // Windowsの「テキストのサイズ」設定で図が崩れないようにする
        };
    }

    private void Place(UIElement e, double cx, double top, double width)
    {
        if (e is FrameworkElement fe) fe.Width = width;
        SetLeft(e, cx - width / 2);
        SetTop(e, top);
        Children.Add(e);
    }

    /// <summary>
    /// 割り当て名の札を作って置く。left/top/widthは、札を置く枠(この中で align に寄せる)。
    /// 文字が無いときは隠れる(Updateが切り替える)。
    /// </summary>
    private void PlaceBadge(Part part, double left, double top, double width, HorizontalAlignment align)
    {
        TextBlock text = MakeText(string.Empty, 17, _badgeText, true);
        text.TextTrimming = TextTrimming.CharacterEllipsis;   // 入りきらない分は末尾を「…」にする
        text.TextWrapping = TextWrapping.NoWrap;
        text.VerticalAlignment = VerticalAlignment.Center;

        var glyph = new Canvas { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var content = new Grid { IsHitTestVisible = false };
        content.Children.Add(text);
        content.Children.Add(glyph);

        var badge = new Border
        {
            Height = BadgeHeight,
            MaxWidth = width,
            CornerRadius = new CornerRadius(BadgeHeight / 2),
            Padding = new Thickness(10, 0, 10, 1),
            Background = _badgeBack,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = content,
            IsHitTestVisible = false,
        };
        var host = new Grid { Width = width, Height = BadgeHeight, IsHitTestVisible = false };
        host.Children.Add(badge);
        SetLeft(host, left); SetTop(host, top);
        Children.Add(host);

        part.Assigned = text;
        part.Badge = badge;
        part.BadgeGlyph = glyph;
    }

    private void AddPill(GamepadButton button, ControllerGlyphs.PadStyle style, double x, double y, double w, double h)
    {
        var shape = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = w, Height = h, RadiusX = h / 2, RadiusY = h / 2, Fill = _fill, Stroke = _line, StrokeThickness = 2,
        };
        SetLeft(shape, x); SetTop(shape, y);
        Children.Add(shape);

        // 左側にボタン名、枠の内側の右に札(割り当て先)
        TextBlock name = MakeText(ControllerGlyphs.GetButtonLabel(button, style), 18, _text, true);
        Place(name, x + 22, y + (h - 24) / 2, 40);

        var part = new Part { Shape = shape, Name = name, IsPill = true };
        PlaceBadge(part, x + 52, y + (h - BadgeHeight) / 2, w - 52 - 6, HorizontalAlignment.Right);
        _parts[button] = part;
    }

    private void AddRound(GamepadButton button, ControllerGlyphs.PadStyle style, double cx, double cy, double r,
        string? fixedName, char side)
    {
        // 光る輪とその外側の光(選択中だけ見える。ボタン本体より先に置いて、後ろに回す)
        var halo = new Ellipse { Width = (r + 15) * 2, Height = (r + 15) * 2, Fill = _blue, Opacity = 0, IsHitTestVisible = false };
        SetLeft(halo, cx - r - 15); SetTop(halo, cy - r - 15);
        Children.Add(halo);
        var ring = new Ellipse { Width = (r + 7) * 2, Height = (r + 7) * 2, Stroke = _blue, StrokeThickness = 3, Opacity = 0, IsHitTestVisible = false };
        SetLeft(ring, cx - r - 7); SetTop(ring, cy - r - 7);
        Children.Add(ring);

        var shape = new Ellipse { Width = r * 2, Height = r * 2, Fill = _fill, Stroke = _line, StrokeThickness = 2 };
        SetLeft(shape, cx - r); SetTop(shape, cy - r);
        Children.Add(shape);

        var part = new Part { Shape = shape, Ring = ring, Halo = halo };
        TextBlock? name = null;
        bool psFace = fixedName is null && style == ControllerGlyphs.PadStyle.PlayStation
            && button is GamepadButton.A or GamepadButton.B or GamepadButton.X or GamepadButton.Y;
        if (psFace)
        {
            AddPsSymbol(part, button, cx, cy, r);
        }
        else
        {
            // 文字はボタンの中央に置く(Gridの中で縦横中央)
            name = MakeText(fixedName ?? ControllerGlyphs.GetButtonLabel(button, style), r > 25 ? 18 : 22, _text, true);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.VerticalAlignment = VerticalAlignment.Center;
            var grid = new Grid { Width = r * 2, Height = r * 2, IsHitTestVisible = false };
            grid.Children.Add(name);
            SetLeft(grid, cx - r); SetTop(grid, cy - r - 1);
            Children.Add(grid);
        }

        // 札は、ボタンの外側へ同じ距離(輪の外側)で置く
        const double gap = 6;
        switch (side)
        {
            case 'T': PlaceBadge(part, cx - 80, cy - r - gap - BadgeHeight, 160, HorizontalAlignment.Center); break;
            case 'L': PlaceBadge(part, cx - r - gap - 150, cy - BadgeHeight / 2, 150, HorizontalAlignment.Right); break;
            case 'R': PlaceBadge(part, cx + r + gap, cy - BadgeHeight / 2, Math.Max(60, 598 - (cx + r + gap)), HorizontalAlignment.Left); break;
            default: PlaceBadge(part, cx - 80, cy + r + gap, 160, HorizontalAlignment.Center); break;
        }

        part.Name = name;
        _parts[button] = part;
    }

    /// <summary>PSのフェイスボタンの記号を、大きさをそろえた図形で描く(下=✕、右=○、左=□、上=△)。</summary>
    private void AddPsSymbol(Part part, GamepadButton button, double cx, double cy, double r)
    {
        char kind = button switch { GamepadButton.A => 'X', GamepadButton.B => 'C', GamepadButton.X => 'S', _ => 'T' };
        foreach (Shape sh in PsGlyph.Create(kind, cx, cy, r * 0.46, _text, 2.4))
        {
            Children.Add(sh);
            part.Glyph.Add(sh);
        }
    }

    private void AddSmall(GamepadButton button, ControllerGlyphs.PadStyle style, double cx, double cy)
    {
        var shape = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 44, Height = 14, RadiusX = 7, RadiusY = 7, Fill = _fill, Stroke = _line, StrokeThickness = 2,
        };
        SetLeft(shape, cx - 22); SetTop(shape, cy - 7);
        Children.Add(shape);

        TextBlock name = MakeText(ControllerGlyphs.GetButtonLabel(button, style), 14, _text, false);
        Place(name, cx, cy - 28, 90);

        var part = new Part { Shape = shape, Name = name };
        PlaceBadge(part, cx - 54, cy + 12, 108, HorizontalAlignment.Center);
        _parts[button] = part;
    }

    /// <summary>割り当て先の表示と、光らせるボタンを更新する。assignedは物理ボタン→コンソールのボタン名(無ければ空)。</summary>
    public void Update(Func<GamepadButton, string> assigned, ICollection<GamepadButton> highlighted)
    {
        // 右スティックの方向の割り当ては、右スティックそのものを光らせる
        bool rightStick = false;
        foreach (GamepadButton b in highlighted)
        {
            if (b is GamepadButton.RightStickUp or GamepadButton.RightStickDown
                or GamepadButton.RightStickLeft or GamepadButton.RightStickRight) rightStick = true;
        }

        var lit = new HashSet<GamepadButton>();
        foreach (KeyValuePair<GamepadButton, Part> kv in _parts)
        {
            if (highlighted.Contains(kv.Key) || (rightStick && kv.Key == GamepadButton.RightThumbstickButton)) lit.Add(kv.Key);
        }

        // 光るボタンの組が変わったときだけ、アニメーションを作り直す(割り当て変更の再描画で点滅がリセットされないように)
        bool changed = !lit.SetEquals(_litNow);
        if (changed)
        {
            _pulse?.Stop();
            _pulse = null;
            _litNow = lit;

            // 前の光りを消す
            foreach (Part p in _parts.Values)
            {
                if (p.Ring is not null) p.Ring.Opacity = 0;
                if (p.Halo is not null) p.Halo.Opacity = 0;
                p.Shape.Fill = _fill;
            }

            if (lit.Count > 0)
            {
                var sb = new Storyboard();
                TimeSpan pulse = TimeSpan.FromSeconds(PulseSeconds);
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

                void AddOpacity(UIElement target, double from, double to)
                {
                    var anim = new DoubleAnimation
                    {
                        From = from, To = to, Duration = new Duration(pulse),
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease,
                    };
                    Storyboard.SetTarget(anim, target);
                    Storyboard.SetTargetProperty(anim, "Opacity");
                    sb.Children.Add(anim);
                }

                foreach (GamepadButton b in lit)
                {
                    Part p = _parts[b];
                    if (p.Ring is not null && p.Halo is not null)
                    {
                        // 丸いボタン: 塗りはそのまま、青い輪とやわらかい光をゆっくり脈打たせる
                        AddOpacity(p.Ring, 0.35, 1.0);
                        AddOpacity(p.Halo, 0.06, 0.24);
                    }
                    else
                    {
                        // 細長いボタン: 淡い青の塗りをゆっくり濃淡させる(枠は青)
                        var brush = new SolidColorBrush(_pillLight);
                        p.Shape.Fill = brush;
                        var anim = new ColorAnimation
                        {
                            From = _pillLight, To = _pillDark, Duration = new Duration(pulse),
                            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease,
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

        foreach (KeyValuePair<GamepadButton, Part> kv in _parts)
        {
            Part part = kv.Value;
            bool on = lit.Contains(kv.Key);
            part.Shape.Stroke = on ? _blue : _line;
            part.Shape.StrokeThickness = on ? 3 : 2;
            if (part.Name is not null) part.Name.Foreground = on ? _blue : _text;
            foreach (Shape g in part.Glyph) g.Stroke = on ? _blue : _text;

            // 割り当て名の札: 無ければ隠す。選択中は青の塗り+白文字に反転
            string text = assigned(kv.Key);
            part.Assigned.Text = text;
            // PSの記号(✕○△□)は、文字ではなく大きさをそろえた図形で札に描く
            part.BadgeGlyph.Children.Clear();
            if (PsGlyph.TryGetKind(text, out char kind))
            {
                foreach (Shape sh in PsGlyph.Create(kind, 9, 9, 5.5, on ? _white : _badgeText, 2.2)) part.BadgeGlyph.Children.Add(sh);
                part.BadgeGlyph.Visibility = Visibility.Visible;
                part.Assigned.Visibility = Visibility.Collapsed;
            }
            else
            {
                part.BadgeGlyph.Visibility = Visibility.Collapsed;
                part.Assigned.Visibility = Visibility.Visible;
            }
            part.Badge.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            part.Badge.Background = on ? _blue : _badgeBack;
            part.Assigned.Foreground = on ? _white : _badgeText;
        }
    }
}
