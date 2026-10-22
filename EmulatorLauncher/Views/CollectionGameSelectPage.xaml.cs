using EmulatorLauncher.Controls;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using EmulatorLauncher.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;

namespace EmulatorLauncher.Views;

/// <summary>
/// コレクション専用のゲーム選択ページ(試作/PoC)。
///
/// GameSelectPageは「色・重複XAMLだけを共通化し、アニメーションやグリッド選択ロジックには
/// 触れない」というリスク低減方針で育ててきたページ(PROJECT_NOTES.md参照)なので、
/// StaggeredPanelのような選択ロジックそのものを作り替える変更はGameSelectPageへ直接は
/// 入れず、このページを新設して隔離している。
///
/// GameSelectPage(GridView/ItemsWrapGrid、均等格子)との最大の違いは、十字キーでの移動を
/// 「選択インデックス ± 列数」という算術ではなく、FocusManager.TryMoveFocusによる
/// 空間的な最近傍探索で行っている点。StaggeredPanelは列ごとにカードの高さがバラバラになる
/// ため、GameSelectPageと同じ算術ベースのナビゲーションが成立しないための対応。
///
/// アイテムのホストにはItemsControl+StaggeredPanel(非仮想化)を使っている。当初は
/// ItemsRepeater+StaggeredLayout(仮想化あり)を検討したが、画面外の未実体化要素へは
/// TryMoveFocusが候補を見つけられず失敗することがあるというWinUIの既知の制約があり、
/// 「コレクションは数が少ない」という前提であればItemsControl+StaggeredPanel(全カードを
/// 常に実体化する)の方がシンプルかつ確実と判断して切り替えた。これにより、フォーカス移動の
/// スクロールもScrollViewer.BringIntoViewOnFocusChange(既定true)という標準機能だけで
/// 完結し、GameSelectPageのStartBringIntoView+TargetRect拡張のような手動対応も不要になった。
///
/// 詳細オーバーレイ(X)・ソート(Y長押し)は、GameSelectPageと共通のUserControl
/// (Controls/GameDetailOverlay、Controls/SortOverlay)を使っている。GameSelectPageとの違いは
/// 「結果をグリッドへどう反映するか」の部分だけで、このページでは以下のようにしている。
/// - ソート: StaggeredPanelは並びが変わるとほぼ全カードの位置が大きく変わり、GameSelectPageの
///   ような「カードが滑って移動する」アニメーションの効果が薄い。またItemsControlでMoveすると
///   コンテナが作り直されてフォーカスが外れるため、Gamesを丸ごと差し替えて先頭へ再フォーカスする。
/// - 詳細: 「選択中カード=XAMLフォーカスを持つButton」なので、閉じたときにそのButtonへ
///   フォーカスを戻す。また表示中のコレクション自身から外されたゲームは、閉じたタイミングで
///   一覧から取り除く(<see cref="ApplyPendingRemovals"/>)。
/// </summary>
public sealed partial class CollectionGameSelectPage : Page, IControllerPage
{
    private enum Mode { Grid, Detail, Sort }

    /// <summary>ゲーム一覧。GameSelectPageと同様、Games自体を新しいインスタンスへ差し替えた際は
    /// GameItemsControl.ItemsSourceも明示的に張り替える(x:Bindの既定はOneTimeのため)。</summary>
    public ObservableCollection<RomFile> Games { get; private set; } = new();

    private RomCollectionInfo _collection = new();
    private RomFile? _selectedGame;
    private Mode _mode = Mode.Grid;

    /// <summary>コレクションへの登録順(RomCollectionInfo.GameIdsの順)に並べたゲーム一覧。
    /// ソートの「お好きな順」を選んだときに、この並びへ戻すために保持しておく。</summary>
    private List<RomFile> _registeredOrder = new();

    /// <summary>詳細オーバーレイで「表示中のこのコレクション」から外されたゲーム。
    /// 詳細を開いている最中に一覧から消すと、裏のカードのフォーカス・選択状態が崩れるため、
    /// オーバーレイを閉じたタイミングでまとめて取り除く(同じ詳細の中で再度追加し直した場合は
    /// ここから外すので、一覧にも残る)。</summary>
    private readonly HashSet<RomFile> _pendingRemovals = new(ReferenceEqualityComparer.Instance);

    /// <summary>読み込み直後に自動フォーカスするためのフラグ。
    /// LoadGamesAsyncでfalseへ戻し、GameCard_Loadedで対象アイテムのButtonが実際に
    /// 読み込まれたタイミングで一度だけtrueにする(詳細はGameCard_Loadedのコメント参照)。</summary>
    private bool _hasSetInitialFocus;

    /// <summary>
    /// 読み込み直後に自動フォーカスする対象のRomFile.No。通常は先頭アイテム
    /// (Games[0].No)だが、GamePlayPageから戻ってきた直後は直前に選んでいたゲームの
    /// Noになる(LoadGamesAsync側で決定する。詳細は<see cref="_pendingRestoreGameNo"/>と
    /// LoadGamesAsyncのコメント参照)。GameCard_Loadedはこの値と一致するButtonが
    /// 生成された時だけフォーカスする。</summary>
    private int? _initialFocusTargetNo;

    /// <summary>
    /// ゲーム開始時(<see cref="StartGame"/>)に選択していたRomFile.Noを、GamePlayPageから
    /// Frame.GoBackで戻ってきた際にフォーカスを復元するために一時的に覚えておく場所。
    /// GameSelectPage._pendingRestoreGameNoと同じ考え方(このページもNavigationCacheModeを
    /// 既定=Disabledのままにしているため、GoBackのたびに新しいインスタンスが生成し直される)。
    /// 消費はLoadGamesAsync側で一覧を読み込み直した直後に一度だけ行い、使ったら必ずnullへ戻す。
    /// </summary>
    private static int? _pendingRestoreGameNo;

    public CollectionGameSelectPage()
    {
        this.InitializeComponent();

        // ダークテーマのときは、ヘッダーの背景帯(明るいグレー→透明)を暗い色にする。
        // 明るいままだと、ダーク用の明るい文字色のタイトルが読めなくなる。
        if (AppThemeRouter.IsDarkActive)
        {
            HeaderFadeStop0.Color = Windows.UI.Color.FromArgb(0xF0, 0x14, 0x16, 0x1B);
            HeaderFadeStop1.Color = Windows.UI.Color.FromArgb(0xD0, 0x14, 0x16, 0x1B);
            HeaderFadeStop2.Color = Windows.UI.Color.FromArgb(0x00, 0x14, 0x16, 0x1B);
        }

        DetailOverlay.Closed += DetailOverlay_Closed;

        // アプリ設定「ゲーム起動方法」: 詳細画面から起動する場合は、詳細内の「起動」で開始し、
        // Xボタンの「詳細」の案内は出さない。
        _launchViaDetail = AppPreferencesStore.Load().LaunchViaDetail;
        DetailOverlay.LaunchRequested += (_, _) => StartGame(DetailOverlay.CoverElement);
        // 「再開」: 前回「タイトル選択にもどる」で保存した状態から続きで起動する。
        DetailOverlay.ResumeRequested += (_, _) =>
        {
            GamePlayPage.ResumeOnNextLaunch = true;
            GamePlayPage.ResumeStateKey = null;
            StartGame(DetailOverlay.CoverElement);
        };
        // ステート画像を選んだとき: そのステート(中断/オート/スロット)から続きで起動する。
        DetailOverlay.StateResumeRequested += (_, key) =>
        {
            GamePlayPage.ResumeOnNextLaunch = true;
            GamePlayPage.ResumeStateKey = key;
            StartGame(DetailOverlay.CoverElement);
        };
        if (_launchViaDetail && CollectionHintBar.Hints.FirstOrDefault(h => h.Key == "X") is { } xHint)
            CollectionHintBar.Hints.Remove(xHint);
        DetailOverlay.MembershipChanged += DetailOverlay_MembershipChanged;
        // 複数枚(ディスク)をまとめた/解除した: 一覧のゲームが増減するので、一覧を読み直す。
        DetailOverlay.DiscSetChanged += (_, _) => _ = LoadGamesAsync(_collection);
        SortOverlay.Closed += (_, _) => _mode = Mode.Grid;
        SortOverlay.SortRequested += SortOverlay_SortRequested;

        // マウス/タッチ用のボタン(ゲーム一覧を操作できる状態のときだけ受け付ける)。
        TouchNav.BackRequested += (_, _) => { if (CanUseTouchNav()) Frame.GoBack(); };
        TouchNav.ConsoleRequested += (_, _) => { if (CanUseTouchNav()) EmulatorLauncher.Controls.TouchNavButtons.GoBackToConsole(Frame); };
        TouchNav.SortRequested += (_, _) => { if (CanUseTouchNav()) ShowSort(); };
    }

    /// <summary>ヘッダーのコレクション名の前に出す、コレクションのアイコン(小)を設定する。
    /// アイコン画像はそのまま表示する(画像自体が角丸なので、切り抜きや背景は付けない)。
    /// 画像(IconPath)が無い/読み込めないときだけ、アクセントカラーの角丸の背景+フォルダのグリフを出す。</summary>
    private void UpdateCollectionIcon(RomCollectionInfo collection)
    {
        _iconAccentHex = collection.AccentColorHex;

        if (string.IsNullOrEmpty(collection.IconPath))
        {
            ShowCollectionIconFallback();
            return;
        }

        CollectionIconBorder.Background = null;
        CollectionIconBorder.CornerRadius = new CornerRadius(0);
        CollectionIconGlyph.Visibility = Visibility.Collapsed;
        CollectionIconImage.Visibility = Visibility.Visible;
        CollectionIconImage.Source =
            new Converters.IconPathToImageSourceConverter().Convert(collection.IconPath, typeof(ImageSource), null!, string.Empty)
                as ImageSource;
    }

    private string _iconAccentHex = "#607D8B";

    private void ShowCollectionIconFallback()
    {
        CollectionIconImage.Source = null;
        CollectionIconImage.Visibility = Visibility.Collapsed;
        CollectionIconBorder.CornerRadius = new CornerRadius(10);
        CollectionIconBorder.Background =
            new Converters.HexToBrushConverter().Convert(_iconAccentHex, typeof(Brush), null!, string.Empty) as Brush;
        CollectionIconGlyph.Visibility = Visibility.Visible;
    }

    private void CollectionIconImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ShowCollectionIconFallback();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        TouchNav.RefreshConsoleButton(Frame);

        if (e.Parameter is RomCollectionInfo collection)
        {
            _collection = collection;
            CollectionNameText.Text = collection.Name;
            UpdateCollectionIcon(collection);
            _pendingRemovals.Clear();
            _ = LoadGamesAsync(collection);
        }

        SortOverlay.Reset();
    }

    /// <summary>
    /// コレクション(RomCollectionInfo.GameIds、コンソールをまたぐRomFile.Noの一覧)から
    /// 実際のRomFileをDB経由でまとめて読み込む(GameSelectPage.LoadGamesForCollectionAsyncと
    /// 同じ考え方。このページは独立させる方針のため、あえて重複実装している)。
    /// </summary>
    private async Task LoadGamesAsync(RomCollectionInfo collection)
    {
        IReadOnlyList<RomFile> files;
        try
        {
            files = await GameLibraryFacade.GetFilesByNosAsync(collection.GameIds);
            await GameLibraryFacade.ResolveIconPathsAsync(files);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CollectionGameSelectPage] コレクションのRomFile読み込みに失敗しました: {ex.Message}");
            files = Array.Empty<RomFile>();
        }

        GameListHelper.ApplyAspectRatios(files);

        _registeredOrder = OrderByRegistration(files, collection);

        // 自動フォーカス対象を決める: GamePlayPageから戻ってきた直後(_pendingRestoreGameNoが
        // 立っていて、かつ対象がまだ一覧に存在する)なら、そのゲームへ。それ以外(通常の
        // 新規読み込み、対象が一覧から消えた等)は従来通り先頭アイテムへフォールバックする。
        // カードのLoadedが実際に発火するより前に確定させておくことで、「対象のカードが
        // 一覧に無いためいつまでもフォーカスされない」という事態を避けている
        // (詳細は_initialFocusTargetNo/GameCard_Loadedのコメント参照)。
        if (_pendingRestoreGameNo is int pendingNo
            && _registeredOrder.Any(g => g.No == pendingNo))
        {
            _initialFocusTargetNo = pendingNo;
            RomFile? returnedGame = _registeredOrder.FirstOrDefault(g => g.No == pendingNo);
            if (returnedGame is not null) PlayReturnCoverAnimation(returnedGame);
        }
        else
        {
            _initialFocusTargetNo = _registeredOrder.Count > 0 ? _registeredOrder[0].No : null;
        }
        _pendingRestoreGameNo = null;

        ReplaceGames(_registeredOrder);
    }

    /// <summary>
    /// DBから読み込んだRomFileを、コレクションへの登録順(RomCollectionInfo.GameIdsの順)へ
    /// 並べ直す。GetByNosAsyncがSQLのIN句で取得している場合、戻り値の順序はGameIdsの順と
    /// 一致する保証が無いため、ここで明示的に揃えておく(GameIdsに無いものは末尾)。
    /// </summary>
    private static List<RomFile> OrderByRegistration(IReadOnlyList<RomFile> files, RomCollectionInfo collection)
    {
        var indexByNo = collection.GameIds
            .Distinct()
            .Select((no, index) => (no, index))
            .ToDictionary(t => t.no, t => t.index);

        return files
            .OrderBy(f => indexByNo.TryGetValue(f.No, out int index) ? index : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Gamesを新しいインスタンスへ差し替え、ItemsControlを張り替える(読み込み時とソート時)。
    /// 新しいカードが生成されるとGameCard_Loadedが走り、先頭カードへ一度だけ自動フォーカスする。
    /// </summary>
    private void ReplaceGames(IEnumerable<RomFile> games)
    {
        // 直前まで選択していたゲームの拡大・枠表示フラグを落としておく(差し替え後の一覧にも
        // 同じRomFileインスタンスが含まれるため、残っていると先頭以外のカードが選択枠付きで
        // 表示されてしまう)。
        if (_selectedGame is not null)
        {
            _selectedGame.IsSelected = false;
        }

        Games = new ObservableCollection<RomFile>(games);
        GameItemsControl.ItemsSource = null;
        GameItemsControl.ItemsSource = Games;
        _selectedGame = null;
        _hasSetInitialFocus = false;
    }

    /// <summary>GameSelectPage.GameIcon_ImageFailedと同じ考え方。IconPathの画像読み込みに
    /// 失敗した場合はImageだけ隠し、下のフォールバック(アクセントカラー+タイトル文字)を見せる。</summary>
    private void GameIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------
    // 選択状態
    // ------------------------------------------------------------------

    /// <summary>カード(Button)がフォーカスを受け取るたびに発火する。ItemsControlには
    /// ItemsRepeater.GetElementIndexのような「要素→インデックス」を引く専用APIが無いため、
    /// XAML側でTag="{x:Bind}"としてRomFile自身をButton.Tagへ入れておき、ここから直接取り出す
    /// (GameSelectPageのDetailCollectionsチップ(Tag="{x:Bind}")と同じ考え方)。</summary>
    private void GameCard_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // ★フォーカスを得たカードの親(ContentPresenter)の ZIndex を上げて最前面にする
        if (VisualTreeHelper.GetParent(button) is ContentPresenter presenter)
        {
            Canvas.SetZIndex(presenter, 10);
        }

        // マウス/タッチのクリックでフォーカスが移った場合は、そのクリック(GameCard_Click)では決定しない(1回目は選ぶだけ)。
        _pointerFocusedThisPress = button.FocusState == FocusState.Pointer;

        if (button.Tag is RomFile game)
        {
            SetSelectedGame(game);
        }

        if (button.Content is Border border)
        {
            UpdateCardFocusVisual(border, isFocused: true);
        }

        BringCardIntoView(button);
    }

    /// <summary>
    /// フォーカスしたカードを、拡大分(1.13倍)のはみ出しも含めて画面内に収まるよう
    /// スクロールさせる。GameSelectPage.SetSelectedGameIndexの手法をそのまま移植している。
    ///
    /// GameScrollViewerはBringIntoViewOnFocusChange="False"にしてあるため、既定の
    /// フォーカス連動スクロールは働かない(標準の挙動は「拡大前の素のButtonのレイアウト境界」
    /// だけを基準にした必要最小限のスクロールしかせず、拡大分の余白(GameScrollViewer.Padding)
    /// まで届かずに止まってしまうため、GameSelectPageで直面したのと同じ問題になる)。代わりに
    /// TargetRectをButtonの実寸より一回り(CardScaleOverflowMargin分)大きく指定して手動で
    /// StartBringIntoViewを呼び、拡大分も含めてビューポート内に収まるようにしている。
    /// </summary>
    private void BringCardIntoView(Button button)
    {
        if (button == null || GameScrollViewer == null) return;

        // 1. ScrollViewerの可視領域（Viewport）の高さを取得
        double viewportHeight = GameScrollViewer.ViewportHeight;
        if (viewportHeight <= 0) return;

        // 2. Button の ScrollViewer 内における相対位置（Y座標）を取得
        var transform = button.TransformToVisual(GameScrollViewer);
        Point buttonPos = transform.TransformPoint(new Point(0, 0));

        // カード拡大アニメーション分（1.13倍など）のマージンを上下に確保
        double margin = CardScaleOverflowMargin; // 32.0 程度
        // 画面いっぱいに広げたスクロール領域の上にはヘッダー、右下にはヒントバーが重なって
        // いるため、それぞれ上端/下端へ追加のクリアランスを取る(拡大カードが隠れないように)。
        double cardTop = buttonPos.Y - margin - TopHeaderClearance;
        double cardBottom = buttonPos.Y + button.ActualHeight + margin + BottomHintBarClearance;

        double currentOffset = GameScrollViewer.VerticalOffset;
        double targetOffset = currentOffset;

        // 3. 上側が見切れる場合 -> 上端が画面内に収まるスクロール位置へ調整
        if (cardTop < 0)
        {
            targetOffset = currentOffset + cardTop;
        }
        // 4. 下側が見切れる場合 -> 下端が画面内に収まるスクロール位置へ調整
        else if (cardBottom > viewportHeight)
        {
            targetOffset = currentOffset + (cardBottom - viewportHeight);
        }

        // 5. 端点のクランプ（0 ～ ScrollableHeight に収める）
        targetOffset = Math.Clamp(targetOffset, 0, GameScrollViewer.ScrollableHeight);

        // 6. スクロール位置が変化する場合のみ ChangeView を呼ぶ
        if (Math.Abs(targetOffset - currentOffset) > 1.0)
        {
            GameScrollViewer.ChangeView(
                horizontalOffset: null,
                verticalOffset: targetOffset,
                zoomFactor: null,
                disableAnimation: false);
        }
    }

    /// <summary>選択カードの拡大(1.13倍)によるはみ出し分の見積もり。GameScrollViewer.Padding
    /// (拡大時のクリップ防止用)と同じ値を使う(この値を変える場合は両方合わせて変更すること)。</summary>
    private const double CardScaleOverflowMargin = 32.0;

    /// <summary>画面上部のヘッダー(コレクション名。背景グラデーション帯つき)が重なる高さ。
    /// スクロール領域が画面いっぱいに広がっているため、上方向のBringIntoViewでこの分だけ余裕を取る。</summary>
    private const double TopHeaderClearance = 84.0;

    /// <summary>画面右下に重なる操作ヒントバー(IsOverlay=TrueのHintBar)の高さ。
    /// 下方向のBringIntoViewでこの分だけ余裕を取る。</summary>
    private const double BottomHintBarClearance = 72.0;

    /// <summary>フォーカスが外れたカードを元のサイズ・Z順に戻す
    /// (GameSelectPage.UpdateCardSelectionVisualのisSelected=false相当)。</summary>
    /// <summary>フォーカスが外れたカードを元のサイズ・Z順に戻す
    /// (GameSelectPage.UpdateCardSelectionVisualのisSelected=false相当)。</summary>
    private void GameCard_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // ★ウィンドウが非アクティブ化された（フォーカス移動先が存在しない）場合は縮小しない
        var newFocusedElement = FocusManager.GetFocusedElement(this.XamlRoot);
        if (newFocusedElement == null)
        {
            return;
        }

        // ★フォーカスが外れたカードの親(ContentPresenter)の ZIndex を通常順位に戻す
        if (VisualTreeHelper.GetParent(button) is ContentPresenter presenter)
        {
            Canvas.SetZIndex(presenter, 0);
        }

        if (button.Content is Border border)
        {
            UpdateCardFocusVisual(border, isFocused: false);
        }
    }
    /// <summary>マウス/タッチでカードを直接クリックした場合。ゲームパッド前提のアプリだが、
    /// 念のため対応しておく(A決定と同じ処理)。</summary>
    private void GameCard_Click(object sender, RoutedEventArgs e)
    {
        // フォーカスされていないカードを押した1回目は、フォーカスを移すだけ(誤って開かないように)。
        // フォーカス中のカードをもう一度押すと決定する。
        if (_pointerFocusedThisPress)
        {
            _pointerFocusedThisPress = false;
            return;
        }

        if (_launchViaDetail) ShowDetail();
        else StartGameDirect();
    }

    /// <summary>直前のフォーカス移動がマウス/タッチのクリックによるものか(そのクリックでは決定しない)。</summary>
    private bool _pointerFocusedThisPress;

    private void SetSelectedGame(RomFile game)
    {
        if (ReferenceEquals(_selectedGame, game)) return;

        if (_selectedGame is not null)
        {
            _selectedGame.IsSelected = false;
        }

        game.IsSelected = true;
        _selectedGame = game;
    }

    // ------------------------------------------------------------------
    // フォーカス時の拡大アニメーション(GameSelectPageと同じ見た目)
    // ------------------------------------------------------------------

    /// <summary>カード(Button)が最初に生成された時に一度だけ発火する(ItemsControl+
    /// StaggeredPanelは非仮想化のため要素が使い回されず、ConsoleSelectPage.CardBorder_Loadedと
    /// 同じ「Loaded1回きりで購読すれば十分」なパターンが使える。GridViewのように
    /// ContainerContentChanging/ElementPreparedでの張り替えは不要)。ここでComposition拡大
    /// アニメーションのセットアップを行う。
    ///
    /// あわせて、<see cref="_initialFocusTargetNo"/>と一致するButtonであれば読み込み直後の
    /// 自動フォーカスもここで行う(通常は先頭アイテム、GamePlayPageから戻ってきた直後は
    /// 直前に選んでいたゲーム。値の決定はLoadGamesAsync側で行う)。
    /// 以前は「ItemsSourceを設定した直後にDispatcherQueue.TryEnqueueで1ティックだけ遅らせて
    /// FindFirstFocusableElementを呼ぶ」方式だったが、ItemsControlのコンテナ生成(レイアウト
    /// パス)がそれより遅れて完了するタイミングでは候補が見つからず、ページ表示時に何も
    /// フォーカスされない不具合があった。Loadedはその要素が実際に読み込まれたタイミングで
    /// 確実に発火するため、より確実な方法としてこちらへ寄せている。</summary>
    private void GameCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        if (button.Content is Border border)
        {
            EnsureCardCompositionAnimation(border);
        }

        if (!_hasSetInitialFocus && button.Tag is RomFile game
            && _initialFocusTargetNo is int targetNo && game.No == targetNo)
        {
            _hasSetInitialFocus = true;
            button.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Borderの合成Visualに、Scaleプロパティが変化したときに自動的に滑らかな
    /// アニメーションを行う暗黙アニメーション(ImplicitAnimations)を仕込む
    /// (GameSelectPage.EnsureCardCompositionAnimationと同じ実装。このページは独立させる方針の
    /// ため、あえて重複実装している)。何度呼んでも副作用は無い。</summary>
    private static void EnsureCardCompositionAnimation(Border border)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(border);

        // サイズ変化時(初回レイアウト決定時も含む)にCenterPointを中央に更新する。
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
    /// フォーカス中カードの拡大(Composition Scale、GameSelectPageと同じ1.13倍)。
    ///
    /// 最前面へのZ順の引き上げ(以前はここでCanvas.SetZIndex(button, ...)をしていた)は、
    /// XAML側のItemsControl.ItemContainerStyle(RomFile.IsSelectedを見るBinding)へ移した。
    /// StaggeredPanelの直接の子は、ItemsControlが自動生成するContentPresenter(このButtonは
    /// その中身)であり、Canvas.ZIndexはButton側に付けてもStaggeredPanel上の実際の重なり順には
    /// 反映されない(Buttonの外側、ContentPresenter自身に付ける必要がある)ため。
    /// </summary>
    private static void UpdateCardFocusVisual(Border border, bool isFocused)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(border);
        float scale = isFocused ? 1.13f : 1.0f;
        visual.Scale = new Vector3(scale, scale, 1.0f);
    }

    // ------------------------------------------------------------------
    // ゲームパッド入力
    // ------------------------------------------------------------------

    public void OnGamepadButton(GamepadButton button)
    {
        // タッチパッド(PS4/PS5)でシステム設定パネルを開閉する。パネル表示中は入力をパネルへ渡す。
        if (button == GamepadButton.Touchpad)
        {
            if (SystemSettings.IsOpen) SystemSettings.Close();
            else if (_mode == Mode.Grid) SystemSettings.Show();
            return;
        }
        if (SystemSettings.IsOpen) { SystemSettings.HandleGamepadInput(button); return; }

        switch (_mode)
        {
            case Mode.Grid:
                HandleGridInput(button);
                break;
            case Mode.Detail:
                DetailOverlay.HandleGamepadInput(button);
                break;
            case Mode.Sort:
                SortOverlay.HandleGamepadInput(button);
                break;
        }
    }

    /// <summary>Y長押しでソート(GameSelectPageと同じ操作)。</summary>
    public void OnGamepadButtonLongPress(GamepadButton button)
    {
        if (_mode == Mode.Grid && button == GamepadButton.Y)
        {
            ShowSort();
        }
    }

    private void HandleGridInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadLeft:
                MoveFocus(FocusNavigationDirection.Left);
                break;
            case GamepadButton.DPadRight:
                MoveFocus(FocusNavigationDirection.Right);
                break;
            case GamepadButton.DPadUp:
                MoveFocus(FocusNavigationDirection.Up);
                break;
            case GamepadButton.DPadDown:
                MoveFocus(FocusNavigationDirection.Down);
                break;
            case GamepadButton.X:
                if (!_launchViaDetail) ShowDetail();
                break;
            case GamepadButton.A:
                if (_launchViaDetail) ShowDetail();
                else StartGameDirect();
                break;
            case GamepadButton.B:
                Frame.GoBack();
                break;
        }
    }

    /// <summary>
    /// 空間的に一番近い候補へフォーカスを移動する。GameSelectPage.HandleGridInputの
    /// index±1 / index±_columns(算術ベース)に相当する処理だが、StaggeredPanelは列ごとに
    /// 高さが違うため算術が使えず、代わりに実際のレイアウト座標を見るFocusManager.TryMoveFocus
    /// を使っている。
    ///
    /// ItemsControl+StaggeredPanelは非仮想化(コレクション内の全カードが常に実体化されている)
    /// なので、以前のItemsRepeater+StaggeredLayout版で懸念していた「画面外の未実体化要素へは
    /// 候補が見つからずTryMoveFocusが失敗する」という問題は構造的に起きない。TryMoveFocusが
    /// falseを返すのは「その方向にこれ以上候補が無い」場合のみのはずで、その場合は何もしない
    /// (端まで来たら止まる、という正しい終端動作)。
    ///
    /// フォーカス移動後の画面内スクロールはScrollViewer.BringIntoViewOnFocusChange(既定true)
    /// が自動で行うため、ここでのスクロール処理は不要。
    /// </summary>
    private void MoveFocus(FocusNavigationDirection direction)
    {
        if (Games.Count == 0) return;

        var options = new FindNextElementOptions { SearchRoot = GameItemsControl };

        // まず空間ナビゲーションを試す
        bool moved = FocusManager.TryMoveFocus(direction, options);

        // 下移動(Down)に失敗した場合のフォールバック（最も近い下方向の要素を自前で探す）
        if (!moved && direction == FocusNavigationDirection.Down)
        {
            TryMoveFocusFallbackDown();
        }
    }

    /// <summary>
    /// StaggeredPanelの高低差により TryMoveFocus(Down) が漏れた場合、
    /// 現在のフォーカス位置より「Y座標が下にあり、かつX座標が一番近い」要素へ強制フォーカスする
    /// </summary>
    private void TryMoveFocusFallbackDown()
    {
        var currentFocused = FocusManager.GetFocusedElement(this.XamlRoot) as FrameworkElement;
        if (currentFocused == null) return;

        var currentTransform = currentFocused.TransformToVisual(GameItemsControl);
        Point currentCenter = currentTransform.TransformPoint(new Point(currentFocused.ActualWidth / 2, currentFocused.ActualHeight / 2));

        Button? candidateButton = null;
        double minDistance = double.MaxValue;

        foreach (var game in Games)
        {
            Button? btn = FindCardButton(game);
            if (btn is null || btn == currentFocused) continue;

            var transform = btn.TransformToVisual(GameItemsControl);
            Point center = transform.TransformPoint(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));

            if (center.Y > currentCenter.Y + 20)
            {
                double dx = Math.Abs(center.X - currentCenter.X);
                double dy = center.Y - currentCenter.Y;
                double distance = (dx * 2) + dy;

                if (distance < minDistance)
                {
                    minDistance = distance;
                    candidateButton = btn;
                }
            }
        }

        candidateButton?.Focus(FocusState.Programmatic);
    }

    /// <summary>指定ゲームのカード(DataTemplateのルートであるButton)を取得する。
    /// ItemsControlが生成するContentPresenterの最初の子がButton。見つからなければnull。</summary>
    private Button? FindCardButton(RomFile game)
    {
        if (GameItemsControl.ContainerFromItem(game) is ContentPresenter presenter
            && VisualTreeHelper.GetChildrenCount(presenter) > 0
            && VisualTreeHelper.GetChild(presenter, 0) is Button button)
        {
            return button;
        }
        return null;
    }

    /// <summary>
    /// 指定ゲームのカードへXAMLフォーカスを戻す。オーバーレイを閉じた直後や、一覧からカードを
    /// 取り除いた直後はレイアウトが確定していないことがある(BringCardIntoViewの座標計算が
    /// ずれる)ため、1ティック遅らせてから行う。
    /// </summary>
    private void FocusCard(RomFile? game)
    {
        if (game is null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            FindCardButton(game)?.Focus(FocusState.Programmatic);
        });
    }

    // ------------------------------------------------------------------
    // 詳細オーバーレイ / ソートオーバーレイ(共通UserControl)
    // ------------------------------------------------------------------

    /// <summary>決定(A)で詳細画面を挟む設定か(アプリ設定「ゲーム起動方法」)。</summary>
    private readonly bool _launchViaDetail;

    private void ShowDetail()
    {
        if (_selectedGame is null) return;

        _mode = Mode.Detail;
        DetailOverlay.ReturnTargetResolver = FindGameCoverImage;
        DetailOverlay.Show(_selectedGame, FindGameCoverImage(_selectedGame), showLaunch: _launchViaDetail);
    }

    /// <summary>一覧のカード内のカバー画像(連結アニメーションの出発点/戻り先)。画面外で未生成ならnull。</summary>
    private UIElement? FindGameCoverImage(RomFile game)
    {
        Image? image = EmulatorLauncher.Controls.GameDetailOverlay.FindFirstImage(FindCardButton(game));
        return image is { Visibility: Visibility.Visible, ActualWidth: > 0 } ? image : null;
    }

    /// <summary>ゲーム終了で戻ってきた時、ゲーム画面のカバー画像が元のカードへ縮んで戻る連結アニメーション。
    /// カードが生成されるまで少し待ってから開始する(画面外なら諦める)。</summary>
    private void PlayReturnCoverAnimation(RomFile game)
    {
        try
        {
            var animation = Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView()
                .GetAnimation(GamePlayPage.ReturnCoverKey);
            if (animation is null) return;

            int tries = 0;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.IsRepeating = true;
            timer.Tick += (t, _) =>
            {
                tries++;
                if (FindGameCoverImage(game) is UIElement target)
                {
                    t.Stop();
                    try { animation.TryStart(target); } catch { }
                }
                else if (tries > 30)
                {
                    t.Stop();
                }
            };
            timer.Start();
        }
        catch { }
    }

    private bool CanUseTouchNav() => _mode == Mode.Grid && !DetailOverlay.IsOpen && !SortOverlay.IsOpen && !SystemSettings.IsOpen;

    private void ShowSort()
    {
        if (Games.Count == 0) return;

        _mode = Mode.Sort;
        SortOverlay.Show();
    }

    private void DetailOverlay_Closed(object? sender, EventArgs e)
    {
        _mode = Mode.Grid;

        RomFile? focusTarget = ApplyPendingRemovals() ?? _selectedGame;
        FocusCard(focusTarget);
    }

    /// <summary>
    /// 詳細オーバーレイでコレクションへの所属が変わった。表示中のこのコレクションに関する
    /// 変更だけを見て、外されたゲームを<see cref="_pendingRemovals"/>へ積む。
    ///
    /// DB更新は非同期のため、A→Bを素早く押すとオーバーレイが閉じた後にこの通知が届くことがある。
    /// その場合は閉じる時の一括除去に間に合わないので、ここで即座に除去する。
    /// </summary>
    private void DetailOverlay_MembershipChanged(object? sender, CollectionMembershipChangedEventArgs e)
    {
        if (e.CollectionId != _collection.Id) return;

        if (e.IsMember)
        {
            _pendingRemovals.Remove(e.Game);
        }
        else
        {
            _pendingRemovals.Add(e.Game);
        }

        if (_mode != Mode.Detail)
        {
            RomFile? focusTarget = ApplyPendingRemovals();
            if (focusTarget is not null) FocusCard(focusTarget);
        }
    }

    /// <summary>
    /// <see cref="_pendingRemovals"/>のゲームを一覧から取り除く。
    /// 選択中のゲーム自身が取り除かれる場合は、次のフォーカス先として「後ろ側の最も近い
    /// ゲーム(無ければ前側)」を返す。選択中のゲームが残る場合・何も取り除かなかった場合はnull
    /// (呼び出し側はその場合、元の選択中カードへフォーカスを戻せばよい)。
    /// </summary>
    private RomFile? ApplyPendingRemovals()
    {
        if (_pendingRemovals.Count == 0) return null;

        RomFile? nextFocus = null;
        if (_selectedGame is not null && _pendingRemovals.Contains(_selectedGame))
        {
            int index = Games.IndexOf(_selectedGame);
            for (int i = index + 1; i < Games.Count && nextFocus is null; i++)
            {
                if (!_pendingRemovals.Contains(Games[i])) nextFocus = Games[i];
            }
            for (int i = index - 1; i >= 0 && nextFocus is null; i--)
            {
                if (!_pendingRemovals.Contains(Games[i])) nextFocus = Games[i];
            }

            _selectedGame.IsSelected = false;
            _selectedGame = null;
        }

        foreach (RomFile game in _pendingRemovals)
        {
            Games.Remove(game);
            _registeredOrder.Remove(game);
        }
        _pendingRemovals.Clear();

        return nextFocus;
    }

    /// <summary>ソートオーバーレイでAが押された。「お好きな順」はコレクションへの登録順へ戻す。
    /// 反映方法はクラスコメント参照(差し替え+先頭へ再フォーカス)。</summary>
    private void SortOverlay_SortRequested(object? sender, SortMode mode)
    {
        List<RomFile> ordered = GameListHelper.Order(Games, mode, _registeredOrder);

        // 入れ替え前の各カードの位置を覚えておき、入れ替え後に「元の位置から新しい位置へ滑る」
        // アニメーションをかける(FLIP方式)。画面全体が空になる瞬間を作らない。
        var oldPositions = new Dictionary<RomFile, Point>();
        foreach (RomFile g in Games)
        {
            if (GameItemsControl.ContainerFromItem(g) is FrameworkElement c && c.ActualWidth > 0)
            {
                oldPositions[g] = c.TransformToVisual(GameItemsControl).TransformPoint(new Point(0, 0));
            }
        }

        ReplaceGames(ordered);
        GameItemsControl.UpdateLayout(); // 新しいカードを生成・配置させ、新しい位置を測れるようにする
        AnimateReorder(oldPositions);
    }

    /// <summary>入れ替え後の各カードを、入れ替え前の位置から新しい位置へ滑らせる。</summary>
    private void AnimateReorder(Dictionary<RomFile, Point> oldPositions)
    {
        if (oldPositions.Count == 0) return;
        Compositor? compositor = null;
        CubicBezierEasingFunction? ease = null;

        foreach (RomFile g in Games)
        {
            if (!oldPositions.TryGetValue(g, out Point oldPos)) continue;
            if (GameItemsControl.ContainerFromItem(g) is not FrameworkElement container) continue;

            Point newPos = container.TransformToVisual(GameItemsControl).TransformPoint(new Point(0, 0));
            float dx = (float)(oldPos.X - newPos.X);
            float dy = (float)(oldPos.Y - newPos.Y);
            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) continue;

            Visual visual = ElementCompositionPreview.GetElementVisual(container);
            compositor ??= visual.Compositor;
            ease ??= compositor.CreateCubicBezierEasingFunction(new Vector2(0.15f, 0.85f), new Vector2(0.25f, 1.0f));

            ElementCompositionPreview.SetIsTranslationEnabled(container, true);
            visual.Properties.InsertVector3("Translation", new Vector3(dx, dy, 0));
            Vector3KeyFrameAnimation anim = compositor.CreateVector3KeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(420);
            anim.InsertKeyFrame(1.0f, Vector3.Zero, ease);
            visual.StartAnimation("Translation", anim);
        }
    }

    private async void StartGameDirect()
    {
        RomFile? game = _selectedGame;
        if (game is not null && AppPreferencesStore.Load().ResumeOnDirectLaunch
            && await GameDetailOverlay.HasSuspendStateAsync(game))
            GamePlayPage.ResumeOnNextLaunch = true;
        StartGame();
    }

    private void StartGame(UIElement? coverSource = null)
    {
        if (_selectedGame is null) return;

        // GamePlayPageからFrame.GoBackで戻ってきた時に、このゲームへフォーカスを
        // 復元できるようNo(一意なID)を覚えておく(詳細は_pendingRestoreGameNoの
        // フィールドコメント参照)。
        _pendingRestoreGameNo = _selectedGame.No;

        // カードのカバー画像が、ゲーム画面へ拡大していく連結アニメーションを仕込む。
        try
        {
            if ((coverSource ?? FindGameCoverImage(_selectedGame)) is UIElement cover)
                Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate(GamePlayPage.LaunchCoverKey, cover);
        }
        catch { }

        Frame.Navigate(typeof(GamePlayPage), _selectedGame);
    }
}
