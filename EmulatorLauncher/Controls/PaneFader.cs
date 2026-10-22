using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmulatorLauncher.Controls;

/// <summary>左メニューで右ペインを切り替えるときの「フェードアウト→切り替え→フェードイン」。</summary>
public sealed class PaneFader
{
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(200));

    private int _token;
    private Storyboard? _running;

    /// <summary>表示中のペインをフェードアウトしてから (表示状態の切り替え)を実行し、新しく表示されたペインをフェードインする。</summary>
    public async Task SwitchAsync(Action apply, params FrameworkElement[] panels)
    {
        int token = ++_token;
        StopRunning();

        FrameworkElement? old = panels.FirstOrDefault(p => p.Visibility == Visibility.Visible);
        if (old == null)
        {
            apply();
            foreach (var p in panels) p.Opacity = 1;
            return;
        }

        await FadeAsync(old, old.Opacity, 0, FadeOutDuration);
        if (token != _token) return;

        apply();
        foreach (var p in panels) p.Opacity = 1;

        FrameworkElement? shown = panels.FirstOrDefault(p => p.Visibility == Visibility.Visible);
        if (shown == null) return;

        shown.Opacity = 0;
        await FadeAsync(shown, 0, 1, FadeInDuration);
        if (token == _token) shown.Opacity = 1;
    }

    private void StopRunning()
    {
        try { _running?.Stop(); } catch { }
        _running = null;
    }

    private Task FadeAsync(FrameworkElement target, double from, double to, Duration duration)
    {
        var tcs = new TaskCompletionSource();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = new QuadraticEase { EasingMode = to > from ? EasingMode.EaseOut : EasingMode.EaseIn },
            EnableDependentAnimation = false,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(animation);
        sb.Completed += (_, _) => tcs.TrySetResult();
        _running = sb;
        sb.Begin();
        return tcs.Task;
    }
}
