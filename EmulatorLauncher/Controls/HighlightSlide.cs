using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 左メニューで選択行が上下に動いたとき、選択の印(枠/バー)が前の行から新しい行へスッと滑るように見せる。
/// 新しい行の印を「前の行があった位置」から本来の位置へ動かすだけで、前の行の印は通常どおり消える
/// (見た目は、1つの印が移動したように見える)。
/// </summary>
public static class HighlightSlide
{
    private const int DurationMilliseconds = 170;

    /// <summary>
    /// 新しい行(<paramref name="newRow"/>)の印を、前の行(<paramref name="oldRow"/>)の位置から滑らせる。
    /// 行の中の最初のGridの、最初の2つの子(選択枠とバー)を動かす。
    /// </summary>
    public static void SlideRow(UIElement? oldRow, UIElement? newRow)
    {
        if (oldRow is null || newRow is null || ReferenceEquals(oldRow, newRow)) return;
        Grid? grid = FindFirstGrid(newRow);
        if (grid is null) return;
        for (int i = 0; i < Math.Min(2, grid.Children.Count); i++)
            SlideElement(grid.Children[i], oldRow);
    }

    /// <summary><paramref name="target"/>を、<paramref name="oldAnchor"/>の位置から本来の位置へ滑らせる。</summary>
    public static void SlideElement(UIElement? target, UIElement? oldAnchor)
    {
        if (target is null || oldAnchor is null) return;
        try
        {
            // 前の行の位置(新しい要素から見た縦の差)。上にあれば負、下にあれば正。
            float dy = (float)oldAnchor.TransformToVisual(target).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (Math.Abs(dy) < 1f) return;

            ElementCompositionPreview.SetIsTranslationEnabled(target, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(target);
            Compositor compositor = visual.Compositor;
            Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(DurationMilliseconds);
            animation.InsertKeyFrame(0f, new Vector3(0, dy, 0));
            animation.InsertKeyFrame(1f, Vector3.Zero,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f)));
            visual.StartAnimation("Translation", animation);
        }
        catch
        {
            // アニメーションが使えなくても、選択の表示自体は成立している。
        }
    }

    /// <summary>選択の帯に付けるTag(右側の一覧の行テンプレート用)。</summary>
    public const string HighlightTag = "highlight";

    /// <summary>
    /// 右側の一覧(セーブ/ロードのスロット、設定)で、選択が前の行から新しい行へ動いたとき、
    /// 行のテンプレートの中のTag="highlight"の要素(選択の帯)を、前の行の位置から滑らせる。
    /// </summary>
    public static void SlideListRow(ItemsControl list, int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0) return;
        UIElement? oldBar = FindTagged(list.ContainerFromIndex(oldIndex));
        UIElement? newBar = FindTagged(list.ContainerFromIndex(newIndex));
        SlideElement(newBar, oldBar);
    }

    private static UIElement? FindTagged(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is FrameworkElement fe && fe.Tag is string tag && tag == HighlightTag) return fe;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            UIElement? found = FindTagged(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static Grid? FindFirstGrid(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Grid g && g.MinHeight > 0) return g;
            Grid? found = FindFirstGrid(child);
            if (found is not null) return found;
        }
        return null;
    }
}
