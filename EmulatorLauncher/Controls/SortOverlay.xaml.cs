using System;
using System.Collections.Generic;
using System.Numerics;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace EmulatorLauncher.Controls;

/// <summary>
/// ソートオーバーレイの共通UserControl。
///
/// 元々GameSelectPageの中にあった ShowSort / CloseSort / MoveSortSelection と、ApplySortの
/// 「どのソート方法が選ばれたか」を決める部分だけを移したもの。実際の並べ替えは
/// <see cref="SortRequested"/>を受けたページ側が行う(グリッドの実装がページごとに違うため)。
///
/// ページ側の使い方:
/// - Y長押し等で <see cref="Show"/> を呼ぶ。
/// - 表示中のゲームパッド入力は <see cref="HandleGamepadInput"/> へそのまま渡す。
///   Aで <see cref="SortRequested"/> を発火してから閉じ、Bでは何もせず閉じる。
///   どちらの場合も最後に <see cref="Closed"/> が発火する。
/// </summary>
public sealed partial class SortOverlay : UserControl
{
    public List<SortOption> SortOptions { get; } = SampleData.GetSortOptions();

    private int _selectedIndex;

    /// <summary>スライドイン(表示)の時間。</summary>
    private const double SlideInMilliseconds = 280;

    /// <summary>スライドアウト(非表示)の時間。閉じる操作はきびきび見せたいので表示より短め。</summary>
    private const double SlideOutMilliseconds = 220;

    /// <summary>パネルを画面外(右)へ逃がす距離の最低値(1920x1080キャンバス上の座標)。
    /// 実際の距離は<see cref="ComputeSlideDistance"/>でウィンドウの右端まで届くよう毎回計算し、
    /// 計算できなかった場合(レイアウト未確定など)だけこの値を使う。</summary>
    private const float MinSlideDistance = 480f;

    /// <summary>パネルがウィンドウ右端を越えた後、さらに余分に動かす量(角丸や影の見切れ防止)。</summary>
    private const double SlideOvershoot = 16.0;

    /// <summary>直近に計算したスライド距離(キャンバス座標)。</summary>
    private float _slideDistance = MinSlideDistance;

    /// <summary>アニメーションの世代番号。Close()のスライドアウト完了時に「その間に再度Show()
    /// されていないか」を判定し、されていればCollapsedにしないために使う。</summary>
    private int _animationVersion;

    /// <summary>現在オーバーレイを表示中かどうか。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Aボタンでソート方法が確定したときに発火する(この直後にClosedも発火する)。</summary>
    public event EventHandler<SortMode>? SortRequested;

    /// <summary>オーバーレイが閉じられたときに発火する(A確定・Bキャンセルのどちらでも)。</summary>
    public event EventHandler? Closed;

    public SortOverlay()
    {
        this.InitializeComponent();
        Visibility = Visibility.Collapsed;

        // XAML要素のVisualでTranslationをアニメーションさせるための事前設定
        // (Offsetはレイアウトのたびにフレームワーク側が書き換えるため使わない)。
        ElementCompositionPreview.SetIsTranslationEnabled(SortPanel, true);

        Reset();
    }

    /// <summary>指定のソート方法を選択状態にする(ゲームから戻ったとき、前回のソートをオーバーレイにも反映するため)。</summary>
    public void SelectMode(SortMode mode)
    {
        int index = SortOptions.FindIndex(o => o.Mode == mode);
        if (index < 0) return;
        foreach (SortOption option in SortOptions) option.IsSelected = false;
        _selectedIndex = index;
        SortOptions[index].IsSelected = true;
    }

    /// <summary>選択状態を先頭(既定のソート方法)へ戻す。ページへの遷移時(OnNavigatedTo)に呼ぶ
    /// (以前GameSelectPage.OnNavigatedToで直接SortOptionsのIsSelectedを書き換えていた処理)。</summary>
    public void Reset()
    {
        foreach (SortOption option in SortOptions) option.IsSelected = false;
        _selectedIndex = 0;
        if (SortOptions.Count > 0) SortOptions[0].IsSelected = true;
    }

    /// <summary>
    /// 表示する。ソート一覧のパネルは画面右からスライドイン、暗幕などの残りはフェードインする。
    ///
    /// 完全に閉じた状態から開く場合は、開始位置(右の画面外・透明)をセットしてから動かす。
    /// スライドアウトの途中で開き直した場合は開始位置をセットし直さず、今の位置から戻ってくる
    /// (各アニメーションは0秒地点のキーフレームを持たないので、現在値から始まる)。
    /// </summary>
    public void Show()
    {
        // 進行中のスライドアウトがあれば、その完了時にCollapsedされないよう世代を進める。
        _animationVersion++;

        Visual rootVisual = ElementCompositionPreview.GetElementVisual(this);
        Visual panelVisual = ElementCompositionPreview.GetElementVisual(SortPanel);
        Compositor compositor = rootVisual.Compositor;

        bool wasCollapsed = Visibility == Visibility.Collapsed;

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        IsOpen = true;

        if (wasCollapsed)
        {
            // Collapsedの間はレイアウトされておらずパネルの位置が測れないため、同期的に1回
            // レイアウトを走らせてからスライド距離を計算する。同じUIスレッドの処理内で開始位置
            // (透明・右の画面外)までセットするので、描画前に一瞬見えてしまうことはない。
            UpdateLayout();
            _slideDistance = ComputeSlideDistance();

            rootVisual.Opacity = 0.0f;
            panelVisual.Properties.InsertVector3("Translation", new Vector3(_slideDistance, 0, 0));
        }

        // 減速カーブ(最初は速く、最後にふわっと止まる)。
        CubicBezierEasingFunction easeOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        rootVisual.StartAnimation("Opacity", CreateOpacityAnimation(compositor, 1.0f, SlideInMilliseconds, easeOut));
        panelVisual.StartAnimation("Translation", CreateTranslationAnimation(compositor, Vector3.Zero, SlideInMilliseconds, easeOut));
    }

    /// <summary>
    /// 閉じる。パネルは右へスライドアウト、残りはフェードアウトし、アニメーション完了後に
    /// Collapsedにする。
    ///
    /// IsOpen=falseとClosedの発火はアニメーションを待たずに即座に行う(ページ側の_modeが
    /// すぐGridへ戻り、スライドアウト中でもグリッド操作ができるように)。スライドアウト中は
    /// IsHitTestVisible=falseにして、マウスクリックが裏のカードへ素通りするようにしている。
    /// </summary>
    public void Close()
    {
        if (!IsOpen) return;

        IsOpen = false;
        IsHitTestVisible = false;
        int version = ++_animationVersion;

        // 開いている間にウィンドウサイズが変わっている可能性があるので、閉じる時にも計算し直す。
        _slideDistance = ComputeSlideDistance();

        Visual rootVisual = ElementCompositionPreview.GetElementVisual(this);
        Visual panelVisual = ElementCompositionPreview.GetElementVisual(SortPanel);
        Compositor compositor = rootVisual.Compositor;

        // 加速カーブ(ゆっくり動き出して、画面外へ向けて速くなる)。
        CubicBezierEasingFunction easeIn = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0.0f), new Vector2(1.0f, 1.0f));

        // ScopedBatchは、この間に開始したアニメーションがすべて終わった(または別のアニメーションで
        // 上書きされて止まった)時点でCompletedを発火する。後者(スライドアウト中にShow()された)の
        // 場合は世代番号が進んでいるので、Collapsedにはしない。
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        rootVisual.StartAnimation("Opacity", CreateOpacityAnimation(compositor, 0.0f, SlideOutMilliseconds, easeIn));
        panelVisual.StartAnimation("Translation", CreateTranslationAnimation(compositor, new Vector3(_slideDistance, 0, 0), SlideOutMilliseconds, easeIn));
        batch.End();

        batch.Completed += (_, _) =>
        {
            // 念のためUIスレッドへ戻してからVisibilityを触る。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _animationVersion)
                {
                    Visibility = Visibility.Collapsed;
                }
            });
        };

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// パネルがウィンドウの右端より外へ完全に出るのに必要な移動量を、1920x1080キャンバスの
    /// 座標系で計算する(Translationはパネル自身の座標系=Viewboxでスケーリングされる前の
    /// キャンバス座標で指定するため)。
    ///
    /// 暗幕をウィンドウ全体へ広げたことで、ウィンドウの縦横比が16:9より横長の場合は
    /// キャンバスの左右に余白(レターボックス)が見えている。固定距離だと、パネルがキャンバスの
    /// 右端を出たところでこの余白部分に残って見えてしまうため、余白の幅も含めて計算する。
    ///
    /// パネル位置はTransformToVisual(XAMLのレイアウト上の位置)で測る。Composition側で設定した
    /// Translationはこの計算に含まれないため、スライドアウト途中などでも正しい値が得られる。
    /// </summary>
    private float ComputeSlideDistance()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || SortPanel.ActualWidth <= 0)
        {
            return MinSlideDistance;
        }

        // Viewbox(Stretch=Uniform)の拡大率と、キャンバス座標で見たウィンドウ右端の位置。
        double scale = Math.Min(ActualWidth / CanvasRoot.Width, ActualHeight / CanvasRoot.Height);
        if (scale <= 0) return MinSlideDistance;

        double letterboxX = (ActualWidth / scale - CanvasRoot.Width) / 2;
        double visibleRight = CanvasRoot.Width + letterboxX;

        Point panelLeft = SortPanel.TransformToVisual(CanvasRoot).TransformPoint(new Point(0, 0));
        double distance = visibleRight - panelLeft.X + SlideOvershoot;

        return (float)Math.Max(distance, MinSlideDistance);
    }

    /// <summary>現在値から指定値へ向かう不透明度アニメーション(0秒地点のキーフレームは置かない)。</summary>
    private static ScalarKeyFrameAnimation CreateOpacityAnimation(
        Compositor compositor, float to, double milliseconds, CompositionEasingFunction easing)
    {
        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.InsertKeyFrame(1.0f, to, easing);
        return animation;
    }

    /// <summary>現在値から指定値へ向かうTranslationアニメーション(0秒地点のキーフレームは置かない)。</summary>
    private static Vector3KeyFrameAnimation CreateTranslationAnimation(
        Compositor compositor, Vector3 to, double milliseconds, CompositionEasingFunction easing)
    {
        Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.InsertKeyFrame(1.0f, to, easing);
        return animation;
    }

    public void HandleGamepadInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                MoveSelection(-1);
                break;
            case GamepadButton.DPadDown:
                MoveSelection(1);
                break;
            case GamepadButton.A:
                if (SortOptions.Count > 0)
                {
                    SortRequested?.Invoke(this, SortOptions[_selectedIndex].Mode);
                }
                Close();
                break;
            case GamepadButton.B:
                Close();
                break;
        }
    }

    /// <summary>並び順の行をクリック/タップしたとき。その並び順を選んで決定する(A=決定と同じ)。</summary>
    private void SortRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!IsOpen) return;
        if (sender is not FrameworkElement { DataContext: SortOption option }) return;

        int index = SortOptions.IndexOf(option);
        if (index < 0) return;

        SortOptions[_selectedIndex].IsSelected = false;
        _selectedIndex = index;
        SortOptions[_selectedIndex].IsSelected = true;
        HandleGamepadInput(GamepadButton.A);
    }

    /// <summary>パネルの外(暗幕の部分)をクリック/タップしたら、何も変えずに閉じる(B=もどると同じ)。</summary>
    private void ScrimRoot_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (!IsOpen) return;

        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, SortPanel) || node is HintBar) return;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }

        e.Handled = true;
        Close();
    }

    private void MoveSelection(int delta)
    {
        if (SortOptions.Count == 0) return;

        SortOptions[_selectedIndex].IsSelected = false;
        _selectedIndex = GamepadListNavigator.Move(_selectedIndex, delta, SortOptions.Count, wrap: false);
        SortOptions[_selectedIndex].IsSelected = true;
    }
}
