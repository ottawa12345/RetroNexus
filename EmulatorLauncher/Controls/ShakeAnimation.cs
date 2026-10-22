using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace EmulatorLauncher.Controls;

/// <summary>「これ以上動かせない」ことを知らせるための、選択枠のブルブル(横に小さく揺れて収まる)アニメーション。</summary>
public static class ShakeAnimation
{
    private const int DurationMilliseconds = 320;

    /// <summary>横揺れの幅(px)。</summary>
    private const float Amplitude = 10f;

    /// <summary>揺らす対象の目印。</summary>
    public const string TargetTag = "ShakeTarget";

    /// <summary>要素を揺らす。</summary>
    public static void Shake(UIElement? container, bool vertical)
    {
        if (container is null) return;
        UIElement element = FindTarget(container) ?? container;

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;

            // 左右へ振れ幅を小さくしながら戻る(減衰振動)。
            Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(DurationMilliseconds);
            // 縦揺れは、行の高さに対して大きく見えやすいので少し小さめにする。
            float a = vertical ? Amplitude * 0.6f : Amplitude;
            Vector3 Offset(float scale) => vertical ? new Vector3(0, a * scale, 0) : new Vector3(a * scale, 0, 0);

            animation.InsertKeyFrame(0.00f, Vector3.Zero);
            animation.InsertKeyFrame(0.15f, Offset(-1f));
            animation.InsertKeyFrame(0.35f, Offset(1f));
            animation.InsertKeyFrame(0.55f, Offset(-0.6f));
            animation.InsertKeyFrame(0.75f, Offset(0.3f));
            animation.InsertKeyFrame(1.00f, Vector3.Zero);

            visual.StartAnimation("Translation", animation);
        }
        catch
        {
            // アニメーションが使えない環境でも、操作自体には影響させない。
        }
    }

    /// <summary>コンテナの子孫から、Tag="ShakeTarget"の要素を探す(無ければnull)。</summary>
    private static UIElement? FindTarget(DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: string tag } fe && tag == TargetTag) return fe;

            UIElement? found = FindTarget(child);
            if (found is not null) return found;
        }

        return null;
    }
}
