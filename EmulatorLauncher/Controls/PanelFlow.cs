using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace EmulatorLauncher.Controls;

/// <summary>
/// ゲーム中のメニューで使う小さなアニメーション部品。
/// <see cref="In"/>: 右側の枠を、上下どちらかから流れ込むように表示する。
/// <see cref="ScaleTo"/>: 要素を中心から拡大/縮小する(巻き戻し画面の選択コマ)。
/// </summary>
public static class PanelFlow
{
    /// <summary>
    /// 枠を流れ込ませる。direction&gt;0(メニューを下へ動かした)なら下から上へ、direction&lt;0なら上から下へ。
    /// </summary>
    public static void In(FrameworkElement? panel, int direction)
    {
        if (panel is null || panel.Visibility != Visibility.Visible || direction == 0) return;
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(panel);
            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Duration = TimeSpan.FromMilliseconds(240);
            slide.InsertKeyFrame(0f, new Vector3(0, Math.Sign(direction) * 90f, 0));
            slide.InsertKeyFrame(1f, Vector3.Zero, ease);
            visual.StartAnimation("Translation", slide);

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(240);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, ease);
            visual.StartAnimation("Opacity", fade);
        }
        catch
        {
            // アニメーションが使えなくても、表示自体は成立している。
        }
    }

    /// <summary>要素を中心から<paramref name="scale"/>倍にする。animate=trueならアニメーションで動かす。</summary>
    public static void ScaleTo(UIElement? element, float scale, bool animate)
    {
        if (element is null) return;
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Windows.Foundation.Size size = element.RenderSize;
            visual.CenterPoint = new Vector3((float)size.Width / 2f, (float)size.Height / 2f, 0);
            Vector3 target = new(scale, scale, 1f);
            if (!animate)
            {
                visual.StopAnimation("Scale");
                visual.Scale = target;
                return;
            }

            Compositor compositor = visual.Compositor;
            Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(180);
            animation.InsertKeyFrame(1f, target,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f)));
            visual.StartAnimation("Scale", animation);
        }
        catch
        {
            // アニメーションが使えなくても、選択の表示自体は成立している。
        }
    }
}
