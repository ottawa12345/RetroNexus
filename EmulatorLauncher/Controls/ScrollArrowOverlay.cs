using EmulatorLauncher.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 横スクロールの一覧(ScrollViewer)の左右の端に、ページ移動の矢印を重ねて出す部品(Netflix/Prime Videoの一覧と同じ)。
/// ・マウスが一覧の上にあるときだけ表示する(タッチでは出さない。スワイプでスクロールできるため)。
/// ・端まで来た側の矢印は出さない。
/// ・矢印を押すと <paramref name="page"/>(-1=前のページ、+1=次のページ)を呼ぶ。見えている枚数ぶんの移動と、
///   フォーカスの移動は各画面の処理で行う(表示とフォーカスが食い違わないようにするため)。
/// 使い方: ScrollViewerと同じ場所(同じGridのセル)にこの部品を追加する。カードのクリックは邪魔しない(矢印の部分だけが当たり判定を持つ)。
/// </summary>
public sealed class ScrollArrowOverlay : Grid
{
    private const double ArrowWidth = 56;

    private readonly ScrollViewer _scroll;
    private readonly Action<int> _page;
    private readonly Border _left;
    private readonly Border _right;
    private readonly bool _showWhilePointerUsed;
    private bool _hover;

    /// <param name="showWhilePointerUsed">
    /// trueなら、マウスを乗せていなくても、最後の操作がマウス/タッチのあいだは矢印を出しっぱなしにする
    /// (タッチでは乗せる操作が無いため。ゲームパッド/キーボード操作のあいだは出さない。端まで来た側は出さない)。
    /// </param>
    public ScrollArrowOverlay(ScrollViewer scroll, Action<int> page, bool showWhilePointerUsed = false)
    {
        _scroll = scroll;
        _page = page;
        _showWhilePointerUsed = showWhilePointerUsed;

        _left = CreateArrow("", HorizontalAlignment.Left, -1);
        _right = CreateArrow("", HorizontalAlignment.Right, +1);
        Children.Add(_left);
        Children.Add(_right);

        // ScrollViewerが処理済みにしたイベントも受け取る(handledEventsToo)。
        scroll.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnPointerEnteredOrMoved), true);
        scroll.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerEnteredOrMoved), true);
        scroll.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnPointerExited), true);
        // 矢印の上にいる間は、ScrollViewerからは出たことになるので、こちらでも見る。
        AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnPointerExited), true);

        if (showWhilePointerUsed)
        {
            Action changed = UpdateVisibility;
            Loaded += (_, _) => { InputDeviceTracker.Changed += changed; UpdateVisibility(); };
            Unloaded += (_, _) => InputDeviceTracker.Changed -= changed;
            scroll.LayoutUpdated += (_, _) => UpdateVisibility();   // 項目数や表示サイズが変わったときに、端の判定をやり直す
        }

        // 一覧が隠れたとき(表示切替で別の並びに変わったときなど)に、矢印だけが残らないようにする。
        scroll.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateVisibility());
        scroll.ViewChanged += (_, _) => UpdateVisibility();
        scroll.SizeChanged += (_, _) => { SyncLayout(); UpdateVisibility(); };
        SyncLayout();
    }

    /// <summary>ScrollViewerが縦に伸びない配置(中央寄せなど)のときは、高さと位置を合わせる。</summary>
    private void SyncLayout()
    {
        if (_scroll.VerticalAlignment == VerticalAlignment.Stretch) return;
        VerticalAlignment = _scroll.VerticalAlignment;
        Height = _scroll.ActualHeight;
        Margin = _scroll.Margin;
    }

    private Border CreateArrow(string glyph, HorizontalAlignment align, int direction)
    {
        bool isLeft = align == HorizontalAlignment.Left;
        var shade = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(isLeft ? 0 : 1, 0),
            EndPoint = new Windows.Foundation.Point(isLeft ? 1 : 0, 0),
        };
        shade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x55, 0, 0, 0), Offset = 0 });
        shade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0, 0, 0), Offset = 1 });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 30,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var border = new Border
        {
            Width = ArrowWidth,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = shade,
            Child = icon,
            Visibility = Visibility.Collapsed,
        };
        border.Tapped += (_, e) =>
        {
            e.Handled = true;
            _page(direction);
        };
        return border;
    }

    private void OnPointerEnteredOrMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || _hover) return;
        _hover = true;
        UpdateVisibility();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        // PointerExitedは子要素の境界を越えるたびにも届く。一覧の範囲の外へ出たときだけ「出た」と扱う。
        Windows.Foundation.Point pos = e.GetCurrentPoint(_scroll).Position;
        if (pos.X >= 0 && pos.Y >= 0 && pos.X < _scroll.ActualWidth && pos.Y < _scroll.ActualHeight) return;
        _hover = false;
        UpdateVisibility();
    }

    /// <summary>ScrollViewer自身、またはその親のどれかがCollapsedなら、実際には見えていない。</summary>
    private bool IsScrollEffectivelyVisible()
    {
        DependencyObject? node = _scroll;
        while (node is not null)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
            node = VisualTreeHelper.GetParent(node);
        }
        return true;
    }

    private void UpdateVisibility()
    {
        // showWhilePointerUsed指定のときは、乗せているかどうかではなく、最後の操作がマウス/タッチかどうかだけで決める
        // (キーボード/パッドに切り替えたら、マウスが乗ったままでも隠す)。
        bool shown = _showWhilePointerUsed
            ? InputDeviceTracker.Current is LastInputDevice.Mouse or LastInputDevice.Touch
            : _hover;
        shown = shown && IsScrollEffectivelyVisible();
        bool canLeft = shown && _scroll.HorizontalOffset > 1;
        bool canRight = shown && _scroll.HorizontalOffset < _scroll.ScrollableWidth - 1;
        _left.Visibility = canLeft ? Visibility.Visible : Visibility.Collapsed;
        _right.Visibility = canRight ? Visibility.Visible : Visibility.Collapsed;
    }

}
