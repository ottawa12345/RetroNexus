using System;
using System.Numerics;
using EmulatorLauncher.Input;
using EmulatorLauncher.Views;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace EmulatorLauncher.Controls;

/// <summary>
/// マウス/タッチ用の画面上のボタン(前の画面 / コンソールに戻る / 並び替え)。
/// マウスやタッチを最後に使ったときだけ表示する(ゲームパッド/キーボード操作の見た目は変えない)。
/// 「コンソールに戻る」は、1つ前の画面がコンソール選択ではないとき(= 前の画面と別の動きになるとき)だけ出す。
/// </summary>
public sealed partial class TouchNavButtons : UserControl
{
    public event EventHandler? BackRequested;
    public event EventHandler? ConsoleRequested;
    public event EventHandler? SortRequested;

    public TouchNavButtons()
    {
        this.InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(RootPanel, true);
        Loaded += (_, _) => { InputDeviceTracker.Changed += OnDeviceChanged; ApplyVisibility(animate: false); };
        Unloaded += (_, _) => InputDeviceTracker.Changed -= OnDeviceChanged;
    }

    /// <summary>並び替えボタンを出すか(並び替えのないページでは false)。</summary>
    public bool ShowSortButton
    {
        get => SortButton.Visibility == Visibility.Visible;
        set => SortButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>画面の履歴から、「コンソールに戻る」を出すか決める。ページ表示時(OnNavigatedTo)に呼ぶ。</summary>
    public void RefreshConsoleButton(Frame? frame)
    {
        ConsoleButton.Visibility = StepsToConsole(frame) > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>コンソール選択画面まで、履歴をさかのぼる回数。コンソール選択が履歴に無ければ0。</summary>
    public static int StepsToConsole(Frame? frame)
    {
        if (frame is null) return 0;
        int steps = 0;
        for (int i = frame.BackStack.Count - 1; i >= 0; i--)
        {
            steps++;
            Type page = frame.BackStack[i].SourcePageType;
            if (page == typeof(ConsoleSelectPage) || page == typeof(ConsoleSelectDarkPage)) return steps;
        }
        return 0;
    }

    /// <summary>コンソール選択画面まで戻る。</summary>
    public static void GoBackToConsole(Frame? frame)
    {
        int steps = StepsToConsole(frame);
        for (int i = 0; i < steps && frame is { CanGoBack: true }; i++) frame.GoBack();
    }

    private bool _shown;
    private int _animationVersion;

    private void OnDeviceChanged() => ApplyVisibility(animate: true);

    /// <summary>マウス/タッチを最後に使ったときだけ表示する。表示/非表示は、フェード+少し上下に動くアニメーションで切り替える。</summary>
    private void ApplyVisibility(bool animate)
    {
        LastInputDevice device = InputDeviceTracker.Current;
        bool want = device is LastInputDevice.Mouse or LastInputDevice.Touch;
        if (want == _shown && (RootPanel.Visibility == Visibility.Visible) == want) return;
        _shown = want;
        int version = ++_animationVersion;

        Visual visual = ElementCompositionPreview.GetElementVisual(RootPanel);
        Compositor compositor = visual.Compositor;

        if (!animate)
        {
            RootPanel.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
            visual.Opacity = want ? 1f : 0f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }

        const float Offset = -10f;   // 隠れているときは、少し上にずらしておく

        if (want)
        {
            if (RootPanel.Visibility == Visibility.Collapsed)
            {
                RootPanel.Visibility = Visibility.Visible;
                visual.Opacity = 0f;
                visual.Properties.InsertVector3("Translation", new Vector3(0, Offset, 0));
            }
            var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
            visual.StartAnimation("Opacity", MakeScalar(compositor, 1f, 220, easeOut));
            visual.StartAnimation("Translation", MakeVector(compositor, Vector3.Zero, 220, easeOut));
        }
        else
        {
            var easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation("Opacity", MakeScalar(compositor, 0f, 160, easeIn));
            visual.StartAnimation("Translation", MakeVector(compositor, new Vector3(0, Offset, 0), 160, easeIn));
            batch.End();
            batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                // 消えている途中でまた表示された場合(世代が進んでいる)は、そのまま表示を続ける。
                if (version == _animationVersion) RootPanel.Visibility = Visibility.Collapsed;
            });
        }
    }

    private static ScalarKeyFrameAnimation MakeScalar(Compositor c, float to, double ms, CompositionEasingFunction easing)
    {
        ScalarKeyFrameAnimation a = c.CreateScalarKeyFrameAnimation();
        a.Duration = TimeSpan.FromMilliseconds(ms);
        a.InsertKeyFrame(1f, to, easing);
        return a;
    }

    private static Vector3KeyFrameAnimation MakeVector(Compositor c, Vector3 to, double ms, CompositionEasingFunction easing)
    {
        Vector3KeyFrameAnimation a = c.CreateVector3KeyFrameAnimation();
        a.Duration = TimeSpan.FromMilliseconds(ms);
        a.InsertKeyFrame(1f, to, easing);
        return a;
    }

    private void BackButton_Tapped(object sender, TappedRoutedEventArgs e) { e.Handled = true; BackRequested?.Invoke(this, EventArgs.Empty); }
    private void ConsoleButton_Tapped(object sender, TappedRoutedEventArgs e) { e.Handled = true; ConsoleRequested?.Invoke(this, EventArgs.Empty); }
    private void SortButton_Tapped(object sender, TappedRoutedEventArgs e) { e.Handled = true; SortRequested?.Invoke(this, EventArgs.Empty); }
}
