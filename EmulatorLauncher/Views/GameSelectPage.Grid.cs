using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;
using EmulatorLauncher.Controls;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.Converters;
using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using EmulatorLauncher.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;

namespace EmulatorLauncher.Views;

// GameSelectPage の一部(partial class)。年代ラベル・仮想化・ゲームグリッドの選択制御。
// 動作は変えず、大きくなったコードビハインドを役割ごとに分けただけ。
public sealed partial class GameSelectPage
{
    // ==================================================================
    // 発売年ソート時の「年代ラベル」(画面左側)
    // ==================================================================
    //
    // 「発売された順」のときだけ、グリッド左側の余白に、その段(行)のゲームの発売年を出す。
    // - 前の段の最後のゲームと同じ年だけが続く段にはラベルを出さない(年が変わるまで出さない)。
    // - 段の中に複数の年が含まれる場合は「1986〜1990年」のように範囲で出す。
    // - 発売年が不明(0以下)のゲームだけの段は「年不明」。
    //
    // ラベルはGridViewのコンテナ(仮想化される)には載せず、GridViewの上に重ねたCanvasへ
    // 表示中の段ぶんだけ(数個)描く。カード1枚の高さ(_gridItemHeight)が固定なので、
    // 段rのY座標は「上余白+r×高さ−スクロール量」で計算できる。
    // 段ごとの文字列は、並び・列数が変わったとき(RebuildYearLabels)に1回だけ作り直す(O(件数))。
    // スクロール中は、その配列から表示範囲を引いて位置を更新するだけ(UpdateYearGutter)。

    /// <summary>年代ラベル用に、グリッド左側へ確保する幅(Paddingの左)。右側は余白を
    /// 最小(YearGutterRightPadding)にして、カードの並びのぶんだけ広く使う。</summary>
    private const double YearGutterWidth = 132;

    private static readonly Thickness NormalGridPadding = new(24, 96, 24, 84);
    /// <summary>年代ラベル表示中の右の余白(通常は24)。右側をなるべく詰めるため小さくしている。</summary>
    private const double YearGutterRightPadding = 4;

    private static readonly Thickness YearGutterGridPadding = new(YearGutterWidth, 96, YearGutterRightPadding, 84);

    /// <summary>現在の、グリッド左端から最初のカード(の枠)までの余白。年代ラベルはこの中に収める。</summary>
    private double _yearGutterMargin = YearGutterWidth;

    /// <summary>段ごとのラベル。nullはその段にラベルを出さない。</summary>
    private string?[] _yearRowLabels = Array.Empty<string?>();

    private readonly List<PixelYearLabel> _yearLabelPool = new();

    /// <summary>左のガター(年代/あ行…のラベル)を出すソート方法か。「発売された順」は年代、「あいうえお順」は行。</summary>
    private bool IsYearGutterMode => _currentSortMode is SortMode.ReleaseDate or SortMode.AlphabeticalKana;

    private bool IsKanaGutterMode => _currentSortMode == SortMode.AlphabeticalKana;

    /// <summary>
    /// 「発売された順」のときだけ、左のPaddingを広げて年代ラベルの幅を確保する(右側は通常のまま)。
    /// それ以外のソート方法では通常のPaddingへ戻す。
    /// </summary>
    private void UpdateYearGutterPadding()
    {
        Thickness desired = IsYearGutterMode ? YearGutterGridPadding : NormalGridPadding;
        if (!GameGridView.Padding.Equals(desired))
        {
            GameGridView.Padding = desired;
        }
    }

    /// <summary>ソート方法が変わったときに、ラベルを作り直す。</summary>
    private void ApplyYearGutterMode()
    {
        UpdateYearJumpHint();
        // 余白(Padding)・列数・ラベルの再計算をまとめて行う。
        RecalculateColumns();
    }

    /// <summary>Gamesの並びと列数から、段ごとのラベル文字列を作り直す。</summary>
    private void RebuildYearLabels()
    {
        if (!IsYearGutterMode || Games.Count == 0)
        {
            _yearRowLabels = Array.Empty<string?>();
            return;
        }

        int columns = Math.Max(1, _columns);
        int rowCount = (Games.Count + columns - 1) / columns;
        var labels = new string?[rowCount];

        if (IsKanaGutterMode)
        {
            RebuildKanaLabels(labels, columns);
            _yearRowLabels = labels;
            return;
        }

        bool previousRowAllUnknown = false;

        for (int row = 0; row < rowCount; row++)
        {
            int start = row * columns;
            int end = Math.Min(start + columns, Games.Count);

            int? previousLastYear = row == 0 ? null : Games[start - 1].ReleaseYear;

            bool hasNewYear = row == 0;
            int min = int.MaxValue;
            int max = int.MinValue;
            bool anyKnown = false;
            for (int i = start; i < end; i++)
            {
                int year = Games[i].ReleaseYear;
                if (previousLastYear != year) hasNewYear = true;
                if (year > 0)
                {
                    anyKnown = true;
                    // 前の段の最後と同じ年は、前の段のラベルに含まれているので範囲から外す
                    // (例: 前の段が1990なら「1990〜1991」ではなく「1991」)。
                    if (year == previousLastYear) continue;
                    if (year < min) min = year;
                    if (year > max) max = year;
                }
            }

            // 年不明のゲームは末尾にまとまる。年のある段から不明へ切り替わる段は、不明が段の途中から
            // 始まるため、その次の段(不明だけの段)にも「年不明」を出す。
            bool allUnknown = !anyKnown;
            if (allUnknown && !previousRowAllUnknown) hasNewYear = true;
            previousRowAllUnknown = allUnknown;

            if (!hasNewYear) continue; // 前の段と同じ年だけが続く間はラベルを出さない

            // 前の段と同じ年+年不明だけの段: 年は前の段のラベルに含まれ、年不明は次の段に出す。
            if (max < 0 && anyKnown) continue;

            if (max < 0)
            {
                labels[row] = "年不明";
            }
            else if (min == max)
            {
                labels[row] = min.ToString(); // ドット絵ラベル(PixelYearLabel)が描く。「年」は付けない
            }
            else
            {
                labels[row] = $"{min}~{max}"; // 範囲は上の年・〜・下の年の縦組みで描く
            }
        }

        _yearRowLabels = labels;
    }

    /// <summary>
    /// 「あいうえお順」用の段ラベル。新しい行(あ行/か行/…/数字/A〜Z/その他)が始まる段にだけ、その行名を出す。
    /// 1つの段の中で複数の行が新しく始まる場合は、「や~ら」のように最初と最後の行名をつなげて出す
    /// (どの行にもZR/ZLで飛べるよう、行が1つも抜けないようにするため)。
    /// </summary>
    private void RebuildKanaLabels(string?[] labels, int columns)
    {
        int previousGroup = -1;
        for (int row = 0; row < labels.Length; row++)
        {
            int start = row * columns;
            int end = Math.Min(start + columns, Games.Count);

            int first = -1;
            int last = -1;
            for (int i = start; i < end; i++)
            {
                int group = GameListHelper.GetKanaGroupIndex(Games[i].SortKey);
                if (group != previousGroup)
                {
                    if (first < 0) first = group;
                    last = group;
                    previousGroup = group;
                }
            }

            if (first < 0) continue;

            labels[row] = first == last
                ? GameListHelper.KanaGroupLabels[first]
                : $"{GameListHelper.ShortKanaGroupLabel(first)}~{GameListHelper.ShortKanaGroupLabel(last)}";
        }
    }

    /// <summary>表示中の段のラベルだけを、現在のスクロール位置に合わせて配置する。</summary>
    private void UpdateYearGutter()
    {
        if (_yearRowLabels.Length == 0)
        {
            YearGutterCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        YearGutterCanvas.Visibility = Visibility.Visible;

        double itemHeight = Math.Max(1, _gridItemHeight);
        double top = GameGridView.Padding.Top;
        double offset = _gridScrollViewer?.VerticalOffset ?? 0;
        double viewHeight = GameGridView.ActualHeight;

        int firstRow = Math.Max(0, (int)Math.Floor((offset - top) / itemHeight));
        int used = 0;

        // ドット1マスの大きさ。4桁(23マス)が余白に収まる最大の整数px(整数にするとにじまない)。
        // 左端に10px、カード側に少し空きができるよう、余白から14px引いて割る。
        // 余白いっぱいだとカードに近すぎて大きいので、1段階小さくする(132pxの余白で3px → 4桁で約69px)。
        double unit = Math.Clamp(Math.Floor((_yearGutterMargin - 14) / 23) - 2, 3, 5);
        // 白背景では少し濃いシアン、暗い背景では明るいシアン。
        var color = ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 0, 214, 255)
            : Windows.UI.Color.FromArgb(255, 0, 150, 230);

        for (int row = firstRow; row < _yearRowLabels.Length; row++)
        {
            double rowTop = top + row * itemHeight - offset;
            if (rowTop >= viewHeight) break;

            string? text = _yearRowLabels[row];
            if (text is null) continue;

            PixelYearLabel label = GetYearLabel(used++);
            label.SetLabel(text, unit, color);
            // 余白(ガター)の中で左右中央に置く。
            double contentWidth = label.Width - PixelYearLabel.Pad * 2;
            Canvas.SetLeft(label, Math.Max(8, (_yearGutterMargin - contentWidth) / 2) - PixelYearLabel.Pad);
            // 段(カード1枚ぶんの高さ)の縦中央に置く。
            Canvas.SetTop(label, rowTop + itemHeight / 2 - label.ContentHeight / 2 - PixelYearLabel.Pad);
            label.Visibility = Visibility.Visible;
        }

        for (int i = used; i < _yearLabelPool.Count; i++)
        {
            _yearLabelPool[i].Visibility = Visibility.Collapsed;
        }
    }

    private PixelYearLabel GetYearLabel(int index)
    {
        while (_yearLabelPool.Count <= index)
        {
            var label = new PixelYearLabel();
            _yearLabelPool.Add(label);
            YearGutterCanvas.Children.Add(label);
        }
        return _yearLabelPool[index];
    }

    // ==================================================================
    // 仮想化対応: コンテナのリサイクルに合わせて拡大アニメーションを張り替える
    // ==================================================================

    /// <summary>
    /// GridViewはスクロールに応じてコンテナ(GridViewItem/その中のBorder)をリサイクルし、
    /// DataContextだけを差し替えて使い回す。そのため、ConsoleSelectPageのCardBorder_Loaded
    /// (Loadedイベント1回だけで購読する方式)をそのまま使うと、コンテナがリサイクルされた際に
    /// 古いRomFileへ購読したままになったり、新しいRomFileの選択状態がアニメーションに
    /// 反映されなかったりする。
    ///
    /// このイベントはコンテナに割り当てられるRomFileが変わるたび(仮想化で新規に生成された時、
    /// およびリサイクルされて別のRomFileが割り当てられた時)に発生するため、ここで
    /// 「Composition拡大アニメーションのセットアップ」と「IsSelected変更時のハンドラの
    /// 張り替え」を行う。
    /// </summary>
    private void GameGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not GridViewItem container)
            return;

        if (args.InRecycleQueue)
        {
            // 画面外に出てリサイクル待ちになったコンテナ。前に割り当てられていたRomFileへの
            // 購読が残ったままだとメモリリーク・意図しない再アニメーションの原因になるため解除する。
            DetachCardHandler(container);
            return;
        }

        if (args.Item is not RomFile game)
            return;

        if (container.ContentTemplateRoot is not Border border)
        {
            // まだDataTemplateの中身(Border)が生成されていない。生成され次第もう一度呼んでもらう。
            args.RegisterUpdateCallback(GameGridView_ContainerContentChanging);
            return;
        }

        // このコンテナが直前まで別のRomFileに割り当てられていた場合は、まずその購読を解除する
        DetachCardHandler(container);

        EnsureCardCompositionAnimation(border);
        UpdateCardSelectionVisual(container, border, game.IsSelected);

        // ソート時のカード移動アニメーション用。コンテナ(GridViewItem)自身のVisualへ、
        // "Offset"が変化したら自動的に滑らかに追従する暗黙アニメーションを仕込んでおく。
        // ItemsWrapGridはソートでカードの並びが変わると、各コンテナのOffset(パネル内の位置)を
        // 直接書き換えて再配置するが、ImplicitAnimationsが仕込まれていると、その書き換えを
        // 「一瞬で飛ぶ」のではなく「元の位置から新しい位置へ滑るように移動する」アニメーションへ
        // 自動的に差し替えてくれる(公式ブログ「Animations with the Visual Layer」のGridView
        // Layout Animationsサンプルと同じ手法。ObservableCollection.Moveに対してReorderThemeTransition
        // 自体は残念ながら発火しないため、Offsetの暗黙アニメーションで代替している)。
        // 通常のレイアウト時(画面表示直後・戻ってきた時)に付けておくと、初回配置の
        // Offset変化(0,0→本来の位置)までアニメーションされ、カードが上から流れ落ちて見える。
        // そのため、ソート中(_animateOffsets)だけ有効にし、それ以外は外しておく。
        if (_animateOffsets) EnsureContainerPositionAnimation(container);
        else ElementCompositionPreview.GetElementVisual(container).ImplicitAnimations = null;

        PropertyChangedEventHandler handler = (_, propArgs) =>
        {
            if (propArgs.PropertyName == nameof(RomFile.IsSelected))
            {
                // PropertyChangedは常にUIスレッドから発火する想定だが、念のため
                // DispatcherQueue経由にしておく。
                border.DispatcherQueue.TryEnqueue(() => UpdateCardSelectionVisual(container, border, game.IsSelected));
            }
        };
        game.PropertyChanged += handler;
        container.Tag = new CardBinding { Game = game, Handler = handler };
    }

    private static void DetachCardHandler(GridViewItem container)
    {
        if (container.Tag is CardBinding binding)
        {
            binding.Game.PropertyChanged -= binding.Handler;
            container.Tag = null;
        }
    }

    /// <summary>Borderの合成Visualに、Scaleプロパティが変化したときに自動的に滑らかな
    /// アニメーションを行う暗黙アニメーション(ImplicitAnimations)を仕込む。これにより、
    /// 以降 <see cref="UpdateCardSelectionVisual"/> で単純にVisual.Scaleへ代入するだけで拡大縮小が
    /// アニメーションするようになる(ConsoleSelectPage.CardBorder_Loadedと同じ手法)。
    /// 何度呼んでも副作用は無いので、コンテナがリサイクルされるたびに呼び直して問題ない。</summary>
    /// 
    /*
    private static void EnsureCardCompositionAnimation(Border border)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(border);

        // border.Width/HeightはAspectRatioToCardSizeConverter経由のx:Bindで確定値が入る想定だが、
        // 念のためNaNガードを入れておく(ConsoleSelectPage.AnimateIconScale/CardBorder_Loadedと
        // 同じ対策。CenterPointにNaNが渡るとComposition側がネイティブ例外でアプリごと落ちるため)。
        double width = double.IsNaN(border.Width) ? border.ActualWidth : border.Width;
        double height = double.IsNaN(border.Height) ? border.ActualHeight : border.Height;
        visual.CenterPoint = new Vector3((float)width / 2, (float)height / 2, 0);

        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f),
            new Vector2(0.25f, 1.0f)
        );

        Vector3KeyFrameAnimation scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Target = "Scale";
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(180);
        scaleAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);

        ImplicitAnimationCollection implicitAnimations = compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Scale"] = scaleAnimation;
        visual.ImplicitAnimations = implicitAnimations;
    }
    */

    private static void EnsureCardCompositionAnimation(Border border)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(border);

        // サイズ変化時（初回レイアウト決定時も含む）に CenterPoint を中央に更新する
        border.SizeChanged += (s, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            }
        };

        // 初期サイズがすでに決まっている場合の即時反映
        if (border.ActualWidth > 0 && border.ActualHeight > 0)
        {
            visual.CenterPoint = new Vector3((float)border.ActualWidth / 2, (float)border.ActualHeight / 2, 0);
        }

        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f),
            new Vector2(0.25f, 1.0f)
        );

        Vector3KeyFrameAnimation scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Target = "Scale";
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(180);
        scaleAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);

        ImplicitAnimationCollection implicitAnimations = compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Scale"] = scaleAnimation;
        visual.ImplicitAnimations = implicitAnimations;
    }

    /// <summary>
    /// 選択中カードの拡大(Composition Scale)に合わせて、コンテナ(GridViewItem)自身の
    /// Z順(Canvas.ZIndex)も引き上げる。
    ///
    /// GridView(ItemsWrapGrid)は既定でItemsのインデックス順にZ順を割り当てるため、何もしないと
    /// 「下の行」「右隣」など後続のインデックスを持つカードが常に手前に描画され、拡大した
    /// カードがそれらの裏に隠れてしまう(はみ出した部分の下端・右端が見えなくなる)。
    /// Canvas.ZIndexはCanvas以外のPanel(ItemsWrapGrid含む)でも子要素の重なり順として
    /// 尊重されるため、選択中だけZIndexを1にして最前面へ引き上げる。
    /// </summary>
    private static void UpdateCardSelectionVisual(GridViewItem container, Border border, bool isSelected)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(border);
        float scale = isSelected ? 1.13f : 1.0f;
        visual.Scale = new Vector3(scale, scale, 1.0f);

        Canvas.SetZIndex(container, isSelected ? 1 : 0);
    }

    /// <summary>コンテナ(GridViewItem)のVisualに、"Offset"プロパティが(パネルの再配置などで)
    /// 変化した際に自動的に滑らかな移動アニメーションを行う暗黙アニメーションを仕込む。
    /// これにより<see cref="ReorderGamesWithAnimation"/>でGamesの並びをMoveしたときに、
    /// ItemsWrapGridが行うカードの再配置(Offsetの書き換え)がアニメーション付きになる。
    /// EnsureCardCompositionAnimationと同じく、何度呼んでも副作用は無い(コンテナがリサイクル
    /// されるたびに呼び直して問題ない)。</summary>
    private static void EnsureContainerPositionAnimation(GridViewItem container)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(container);
        Compositor compositor = visual.Compositor;

        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f),
            new Vector2(0.2f, 1.0f)
        );

        Vector3KeyFrameAnimation offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.Target = "Offset";
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(260);
        offsetAnimation.InsertExpressionKeyFrame(0.0f, "this.StartingValue");
        offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);

        ImplicitAnimationCollection implicitAnimations = compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Offset"] = offsetAnimation;
        visual.ImplicitAnimations = implicitAnimations;
    }

    // ------------------------------------------------------------------
    // ゲームグリッドの選択制御
    // ------------------------------------------------------------------

    /// <param name="bringIntoView">falseなら選択状態だけ更新し、スクロールはしない
    /// (L/Rのページ送りのように、呼び出し側が自分でスクロール位置を決める場合)。</param>
    private void SetSelectedGameIndex(int index, bool bringIntoView = true)
    {
        if (Games.Count == 0) return;
        index = Math.Clamp(index, 0, Games.Count - 1);

        var newGame = Games[index];

        // 前回選択していたアイテムは、インデックスではなくオブジェクト参照(_selectedGame)で
        // 特定する(理由はフィールドのコメント参照)。
        if (_selectedGame is not null && !ReferenceEquals(_selectedGame, newGame))
        {
            _selectedGame.IsSelected = false;
        }

        newGame.IsSelected = true;
        _selectedGame = newGame;
        _selectedGameIndex = index;

        // ヘッダー左上の選択中ゲーム名。タイトルが実際に変わった時だけ、左からスライドインし直す
        // (同じゲームのまま他の状態だけ変わるケースで毎回再アニメーションしないためのガード)。
        if (SelectedGameNameText.Text != newGame.DisplayTitle)
        {
            AnimateGameNameSlideIn(newGame.DisplayTitle);
        }
        UpdateSelectedGameThumb(newGame);
        UpdateSelectedGameBadges(newGame);

        if (bringIntoView)
        {
            BringGameIntoView(newGame);
        }
    }

    /// <summary>ヘッダーのタイトル横に、発売年とプレイ人数の四角いバッジを出す。年が未定(0以下)なら年は出さない。</summary>
    private void UpdateSelectedGameBadges(RomFile? game)
    {
        bool hasYear = game is not null && game.ReleaseYear > 0;
        SelectedGameYearBadge.Visibility = hasYear ? Visibility.Visible : Visibility.Collapsed;
        if (hasYear) SelectedGameYearBadgeText.Text = game!.ReleaseYear.ToString();

        string players = game?.Players?.Trim() ?? string.Empty;
        // 数字だけ("1"など)なら「1人用」の形にする。
        if (players.Length > 0 && players.All(char.IsAsciiDigit)) players += "人用";
        SelectedGamePlayersBadge.Visibility = players.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedGamePlayersBadgeText.Text = players;
    }

    private static readonly IconPathToImageSourceConverter ThumbConverter = new();
    private string? _thumbIconPath;

    /// <summary>ヘッダー左上、タイトルの前に出す、選択中ゲームのパッケージ画像(小)を更新する。
    /// 同じ画像のままなら何もしない。画像が無い(未選択・IconPathが空)ときは隠す。</summary>
    private void UpdateSelectedGameThumb(RomFile? game)
    {
        string? path = game?.IconPath;
        if (string.IsNullOrEmpty(path))
        {
            _thumbIconPath = null;
            SelectedGameThumb.Source = null;
            SelectedGameThumb.Visibility = Visibility.Collapsed;
            return;
        }

        if (path == _thumbIconPath && SelectedGameThumb.Source is not null) return;

        _thumbIconPath = path;
        SelectedGameThumb.Source = ThumbConverter.Convert(path, typeof(Microsoft.UI.Xaml.Media.ImageSource), null!, string.Empty)
            as Microsoft.UI.Xaml.Media.ImageSource;
        SelectedGameThumb.Visibility = SelectedGameThumb.Source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// ヘッダー左上の選択中ゲーム名を、左から滑るように表示する(SortOverlay.Showと同じ、
    /// Composition APIのTranslation/Opacityキーフレームアニメーション)。
    /// </summary>
    private void AnimateGameNameSlideIn(string title)
    {
        SelectedGameNameText.Text = title;

        Visual visual = ElementCompositionPreview.GetElementVisual(SelectedGameNameText);
        Compositor compositor = visual.Compositor;

        CubicBezierEasingFunction easeOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        // 開始位置(左・透明)をセットしてから、現在位置(0・不透明)へアニメーションさせる。
        visual.Properties.InsertVector3("Translation", new Vector3(-GameNameSlideDistance, 0, 0));
        visual.Opacity = 0f;

        Vector3KeyFrameAnimation translationAnimation = compositor.CreateVector3KeyFrameAnimation();
        translationAnimation.Duration = TimeSpan.FromMilliseconds(GameNameSlideInMilliseconds);
        translationAnimation.InsertKeyFrame(1.0f, Vector3.Zero, easeOut);

        ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(GameNameSlideInMilliseconds);
        opacityAnimation.InsertKeyFrame(1.0f, 1.0f, easeOut);

        visual.StartAnimation("Translation", translationAnimation);
        visual.StartAnimation("Opacity", opacityAnimation);
    }

    /// <summary>指定ゲームのカードを、拡大分の余白も含めて画面内へスクロールさせる
    /// (以前はSetSelectedGameIndexの中に直接書かれていた処理を切り出したもの)。</summary>
    private void BringGameIntoView(RomFile newGame)
    {
        if (GameGridView.ContainerFromItem(newGame) is FrameworkElement element)
        {
            // 選択カードはComposition APIで1.13倍に拡大される(UpdateCardSelectionVisual参照)。
            // この拡大はレンダリングだけの変形(Visual.Scale)でレイアウト上のサイズには
            // 影響しないため、素のままStartBringIntoViewを呼ぶと「拡大前のコンテナ境界」だけを
            // 基準にした必要最小限のスクロール量で止まってしまう。その結果、コンテナの端が
            // ちょうどビューポートの端に揃う位置で止まり、GameGridView.Paddingで確保した
            // 拡大分の余白まではスクロールされず、拡大したカードの端が見切れることがある
            // (特に、最初/最後の行が既にビューポート内にあるが位置がギリギリの状態から
            // 選択移動したケースで顕著)。
            // TargetRectをコンテナの実寸より一回り(CardScaleOverflowMargin分)大きく指定する
            // ことで、拡大分も含めてビューポート内に収まるようスクロールさせる。
            // 上方向はヘッダー(TopHeaderClearance)、下方向は右下のヒントバー(BottomHintBarClearance)が
            // グリッドに重なって表示されているため、その分の高さも余分に確保する。
            var inflatedTarget = new Rect(
                -CardScaleOverflowMargin,
                -CardScaleOverflowMargin - TopHeaderClearance,
                element.ActualWidth + CardScaleOverflowMargin * 2,
                element.ActualHeight + CardScaleOverflowMargin * 2 + TopHeaderClearance + BottomHintBarClearance);

            element.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                TargetRect = inflatedTarget,
            });
        }
        else
        {
            // UI仮想化により、対象カードのコンテナがまだ生成されていない場合。
            // ScrollIntoViewでスクロールさせれば、生成され次第
            // GameGridView_ContainerContentChanging側で正しい選択状態(拡大表示)が反映される。
            GameGridView.ScrollIntoView(newGame);
        }
    }

    private void MoveGameSelection(int delta)
    {
        SetSelectedGameIndex(_selectedGameIndex + delta);
    }

    /// <summary>
    /// ZR/ZLで、次/前の年代(年代ラベルのある段)の先頭のゲームへ選択を移し、そこまで高速スクロールする(「発売された順」のときのみ)。
    /// ZLは、今いる年代の途中なら、まずその年代の先頭へ戻り、すでに先頭ならさらに前の年代へ移る。
    /// 年代ラベルの有無は、左側に表示している年代ラベルの配列(_yearRowLabels)をそのまま使う。
    /// </summary>
    private void JumpYearGroup(int direction)
    {
        if (!IsYearGutterMode || _yearRowLabels.Length == 0 || Games.Count == 0) return;

        int columns = Math.Max(1, _columns);
        int row = Math.Clamp(_selectedGameIndex, 0, Games.Count - 1) / columns;
        int target = -1;

        if (direction > 0)
        {
            for (int r = row + 1; r < _yearRowLabels.Length; r++)
            {
                if (_yearRowLabels[r] is not null) { target = r; break; }
            }
        }
        else
        {
            if (row < _yearRowLabels.Length && _yearRowLabels[row] is not null && _selectedGameIndex % columns != 0)
            {
                target = row; // 年代の先頭の段の途中にいる: その段の先頭へ
            }
            else
            {
                for (int r = row - 1; r >= 0; r--)
                {
                    if (_yearRowLabels[r] is not null) { target = r; break; }
                }
            }
        }

        if (target < 0) return;
        StartYearJumpScroll(target, target * columns);
    }

    // ---- ZR/ZLの年代ジャンプ: ワープせず、高速にスクロールして移動する ----

    private DispatcherQueueTimer? _jumpTimer;
    private readonly Stopwatch _jumpClock = new();
    private double _jumpFrom;
    private double _jumpTo;
    private double _jumpCurrent;
    private double _jumpDurationSeconds;

    /// <summary>
    /// 年代の先頭の段(targetRow)が画面の上端付近に来るまで、距離に応じた時間(約0.2〜0.8秒、
    /// 終わりに向けて減速)でスクロールする。選択(タイトル・パッケージ画像・拡大表示)は
    /// 押した時点で移動先へ移し、スクロール中に手前の年代の段が流れていく。
    /// 連続して押された場合は、今のスクロール位置から次の年代へ向けて始め直す。
    /// </summary>
    private void StartYearJumpScroll(int targetRow, int targetIndex)
    {
        ScrollViewer? scrollViewer = _gridScrollViewer;
        if (scrollViewer is null || _gridItemHeight <= 0)
        {
            SetSelectedGameIndex(targetIndex);
            return;
        }

        // 押しっぱなしのL/Rスクロールが動いていたら止める。
        StopHoldScroll(bringIntoView: false);

        double current = _jumpTimer?.IsRunning == true ? _jumpCurrent : scrollViewer.VerticalOffset;
        double maxOffset = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);

        // 年代の先頭の段を、グリッド上端のPadding(96)の位置=最初の段と同じ高さへ合わせる。
        double target = Math.Clamp(targetRow * _gridItemHeight, 0, maxOffset);

        SetSelectedGameIndex(targetIndex, bringIntoView: false);

        double distance = Math.Abs(target - current);
        if (distance < 1)
        {
            StopYearJump(finalize: true);
            return;
        }

        _jumpFrom = current;
        _jumpTo = target;
        _jumpCurrent = current;
        _jumpDurationSeconds = Math.Clamp(distance / 6000.0 + 0.2, 0.22, 0.8);
        _jumpClock.Restart();

        if (_jumpTimer is null)
        {
            _jumpTimer = DispatcherQueue.CreateTimer();
            _jumpTimer.Interval = TimeSpan.FromMilliseconds(16);
            _jumpTimer.IsRepeating = true;
            _jumpTimer.Tick += JumpTimer_Tick;
        }
        _jumpTimer.Start();
    }

    private void JumpTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        ScrollViewer? scrollViewer = _gridScrollViewer;
        if (_mode != Mode.Grid || scrollViewer is null)
        {
            StopYearJump(finalize: false);
            return;
        }

        double t = Math.Min(1.0, _jumpClock.Elapsed.TotalSeconds / _jumpDurationSeconds);
        double eased = 1.0 - Math.Pow(1.0 - t, 3); // 速く動き出して、終わりに向けて減速する
        _jumpCurrent = _jumpFrom + (_jumpTo - _jumpFrom) * eased;
        scrollViewer.ChangeView(null, _jumpCurrent, null, disableAnimation: true);

        if (t >= 1.0)
        {
            StopYearJump(finalize: true);
        }
    }

    /// <param name="finalize">止めた後、選択カードを(拡大分も含めて)画面内に収め直すか。</param>
    private void StopYearJump(bool finalize)
    {
        if (_jumpTimer?.IsRunning == true)
        {
            _jumpTimer.Stop();
        }
        _jumpClock.Stop();

        if (finalize && _selectedGame is not null)
        {
            BringGameIntoView(_selectedGame);
        }
    }

    private HintDefinition? _yearJumpHint;

    /// <summary>「発売された順」のときだけ、操作ヒントへZL/ZR(年代へ移動)を足す。</summary>
    private void UpdateYearJumpHint()
    {
        if (IsYearGutterMode)
        {
            string label = IsKanaGutterMode ? "行へ" : "年代へ";
            if (_yearJumpHint is not null && _yearJumpHint.Label != label)
            {
                GameHintBar.Hints.Remove(_yearJumpHint);
                _yearJumpHint = null;
            }

            if (_yearJumpHint is null)
            {
                _yearJumpHint = new HintDefinition { Key = "L2/R2", Label = label };
                GameHintBar.Hints.Insert(0, _yearJumpHint);
            }
        }
        else if (_yearJumpHint is not null)
        {
            GameHintBar.Hints.Remove(_yearJumpHint);
            _yearJumpHint = null;
        }
    }
}
