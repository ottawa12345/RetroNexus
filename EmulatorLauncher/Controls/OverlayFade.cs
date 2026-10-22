using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace EmulatorLauncher.Controls;

/// <summary>オーバーレイ(GameDetailOverlay、CollectionEditOverlay)共通の、表示/非表示時のフェードイン・フェードアウト。</summary>
public static class OverlayFade
{
    /// <summary>表示時のフェードイン時間。</summary>
    public const double FadeInMilliseconds = 200;

    /// <summary>非表示時のフェードアウト時間(閉じる操作はきびきび見せたいので、表示より少し短め)。</summary>
    public const double FadeOutMilliseconds = 150;

    public static void Attach(UIElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;

        // フェードイン: 常に透明(0)から始める。
        ScalarKeyFrameAnimation fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Target = "Opacity";
        fadeIn.Duration = TimeSpan.FromMilliseconds(FadeInMilliseconds);
        fadeIn.InsertKeyFrame(0.0f, 0.0f);
        fadeIn.InsertKeyFrame(1.0f, 1.0f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f)));

        // フェードアウト: 0秒地点のキーフレームをあえて置かず、「現在の不透明度」から0へ向かう。
        ScalarKeyFrameAnimation fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Target = "Opacity";
        fadeOut.Duration = TimeSpan.FromMilliseconds(FadeOutMilliseconds);
        fadeOut.InsertKeyFrame(1.0f, 0.0f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0.0f), new Vector2(1.0f, 1.0f)));

        ElementCompositionPreview.SetImplicitShowAnimation(element, fadeIn);
        ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);
    }
}
