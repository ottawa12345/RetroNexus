using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 一覧の「フォーカス行(白背景+水色の枠)」が、前のフォーカス行の位置から新しい行へ、なめらかにスライドして移るようにする添付プロパティ。
/// フォーカス行の枠のBorderに <c>fs:FocusSlide.IsFocused="{x:Bind IsFocused, Mode=OneWay}"</c> を付けるだけで使える。
/// 同じScrollViewerの中(=同じ一覧の中)で、前の行がまだ表示されているときだけ動かす。それ以外(画面を開いた直後、別の一覧への切り替えなど)は、その場に出す。
/// </summary>
public static class FocusSlide
{
    private const int Milliseconds = 140;
    private const double MaxDistance = 900;

    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.RegisterAttached("IsFocused", typeof(bool), typeof(FocusSlide), new PropertyMetadata(false, OnIsFocusedChanged));

    public static bool GetIsFocused(DependencyObject d) => (bool)d.GetValue(IsFocusedProperty);
    public static void SetIsFocused(DependencyObject d, bool value) => d.SetValue(IsFocusedProperty, value);

    private static WeakReference<FrameworkElement>? _last;

    private static void OnIsFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (e.NewValue is not true)
        {
            // フォーカスが外れた行は、動きかけの位置(Translation)を残さない。
            // 一覧の行の要素は使い回されるため、途中の位置が残ると別の項目がずれて(残像のように)見える。
            ResetTranslation(element);
            SetRaised(element, false);
            return;
        }

        try
        {
            FrameworkElement? previous = null;
            _last?.TryGetTarget(out previous);
            _last = new WeakReference<FrameworkElement>(element);
            SetRaised(element, true);

            if (previous is null || ReferenceEquals(previous, element)) return;
            if (!element.IsLoaded || !previous.IsLoaded || element.XamlRoot is null || previous.XamlRoot is null) return;
            if (previous.ActualWidth <= 0 || previous.ActualHeight <= 0 || element.ActualWidth <= 0) return;

            // 同じ一覧(同じScrollViewer)の中だけ動かす。
            ScrollViewer? scroller = FindScroller(element);
            if (scroller is null || !ReferenceEquals(scroller, FindScroller(previous))) return;

            UIElement? root = element.XamlRoot.Content;
            if (root is null) return;
            Vector2 now = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).ToVector2();
            Vector2 before = previous.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).ToVector2();

            // 同じ一覧の中の移動なので、縦方向だけ動かす(横のずれは付けない)。
            float dx = 0f;
            float dy = before.Y - now.Y;
            if (Math.Abs(dy) < 1 || Math.Abs(dy) > MaxDistance) return;

            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            _animated.Remove(element);
            _animated.Add(element, new object());
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            visual.Properties.InsertVector3("Translation", new Vector3(dx, dy, 0f));
            Compositor c = visual.Compositor;
            Vector3KeyFrameAnimation animation = c.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(1f, Vector3.Zero, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f)));
            animation.Duration = TimeSpan.FromMilliseconds(Milliseconds);
            visual.StartAnimation("Translation", animation);

            // 枠の中に文字などが入っている行(Borderが中身を包んでいる)は、中身も一緒に動いて「文字が枠についてくる」ように見える。
            // 中身には逆向きの動きを同時に付けて打ち消し、動くのは枠(背景と線)だけにする。
            if (InnerOf(element) is UIElement child)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(child, true);
                Visual cv = ElementCompositionPreview.GetElementVisual(child);
                cv.Properties.InsertVector3("Translation", new Vector3(-dx, -dy, 0f));
                Vector3KeyFrameAnimation counter = c.CreateVector3KeyFrameAnimation();
                counter.InsertKeyFrame(1f, Vector3.Zero, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f)));
                counter.Duration = TimeSpan.FromMilliseconds(Milliseconds);
                cv.StartAnimation("Translation", counter);
            }
        }
        catch (Exception)
        {
            // アニメーションは見た目だけの機能。要素が外れている等で失敗しても、何もしない(その場に出る)。
        }
    }

    private static void ResetTranslation(FrameworkElement element)
    {
        try
        {
            if (!_animated.TryGetValue(element, out _)) return;
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            if (InnerOf(element) is UIElement child)
            {
                Visual cv = ElementCompositionPreview.GetElementVisual(child);
                cv.StopAnimation("Translation");
                cv.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            _animated.Remove(element);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>スライドの動きを付けた要素(外れたときに位置を戻すため)。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> _animated = new();

    /// <summary>
    /// フォーカス行を、並びの手前(重なり順の上)に出す。しないと、上へ動かすとき(後ろの行ほど手前に描かれるため)、
    /// 動いている枠が下の行の背景に隠れて、点滅しているように見える。
    /// </summary>
    private static void SetRaised(FrameworkElement element, bool raised)
    {
        try
        {
            // 重なり順を変えるのは、一覧の「行の入れ物」(ListViewItem、またはItemsControlの行)だけ。
            // 行の中のテンプレート内の要素に付けると、同じ行の文字より手前に出て、文字が隠れてしまう。
            DependencyObject? current = VisualTreeHelper.GetParent(element);
            while (current is not null)
            {
                DependencyObject? parent = VisualTreeHelper.GetParent(current);
                if (current is ListViewItem || (current is ContentPresenter && parent is Panel))
                {
                    Canvas.SetZIndex((UIElement)current, raised ? 100 : 0);
                    return;
                }
                current = parent;
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>枠の中身(Borderの子、またはButtonなどのContent)。</summary>
    private static UIElement? InnerOf(FrameworkElement element) => element switch
    {
        Border b => b.Child,
        ContentControl c => c.Content as UIElement,
        _ => null,
    };

    private static ScrollViewer? FindScroller(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is ScrollViewer s) return s;
        }
        return null;
    }
}
