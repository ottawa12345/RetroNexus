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

/// <summary>
/// 2枚目画像相当: ゲーム選択グリッド。
/// 3枚目(詳細/Xボタン)、4枚目(ソート/Y長押し)のオーバーレイは、CollectionGameSelectPageと
/// 共通のUserControl(Controls/GameDetailOverlay、Controls/SortOverlay)に切り出してある。
/// このページに残っているのは「どちらのオーバーレイを表示中か(_mode)」の管理と、ソート結果を
/// このページのGridViewへアニメーション付きで反映する処理(<see cref="ReorderGamesWithAnimation"/>)だけ。
///
/// 最大1000件程度のROMが並ぶ可能性があるため、以下の2点に対応している。
/// 1. 表示: ItemsControl+外側ScrollViewerではなくGridView(仮想化対応のItemsWrapGrid)を使い、
///    画面に映っている分だけコンテナを生成・リサイクルするUI仮想化を効かせる
///    (<see cref="GameGridView_ContainerContentChanging"/>)。
/// 2. アニメーション: 選択カードの拡大、およびソート時のカード移動は、仮想化下でも正しく
///    動くようComposition APIで実装している(ConsoleSelectPage.CardBorder_Loadedと同じ考え方だが、
///    GridViewはコンテナをリサイクルするため、Loadedイベント1回きりの購読では対応できない。
///    その代わりにContainerContentChangingで「コンテナに今どのRomFileが割り当てられているか」が
///    変わるたびに購読を張り替えている)。
/// </summary>
public sealed partial class GameSelectPage : Page, IControllerPage
{
    private enum Mode { Grid, Detail, Sort }

    /// <summary>GridViewItem.Tagへ、リサイクル対応のためコンテナと現在紐付いている
    /// RomFile/PropertyChangedハンドラを保持しておくための入れ物。
    /// (object型のTagへValueTupleを直接入れてisパターンで取り出すよりも、専用の型を
    /// 用意したほうが意図が明確で、パターンマッチの記述ミスも防げるため。)</summary>
    private sealed class CardBinding
    {
        public required RomFile Game { get; init; }
        public required PropertyChangedEventHandler Handler { get; init; }
    }

    /// <summary>ゲーム一覧。ソート時は原則としてインスタンスを差し替えず、
    /// <see cref="ReorderGamesWithAnimation"/>でMoveのみ行うことで、
    /// GridView(ItemsWrapGrid)標準のReorderThemeTransitionによる
    /// 「カードが元の位置から新しい位置へ滑るように移動する」アニメーションを効かせている。
    /// (Clear+AddやItemsSourceの差し替えだと単なるReset通知になり、アニメーションが失われる)</summary>
    public ObservableCollection<RomFile> Games { get; private set; } = new();

    private ConsoleInfo _console = new();

    /// <summary>コレクション表示のときの、表示中のコレクション(コンソール表示ではnull)。複数枚のまとめ後の読み直しに使う。</summary>
    private RomCollectionInfo? _collectionInfo;
    private Mode _mode = Mode.Grid;
    private int _selectedGameIndex = -1;

    /// <summary>ゲームカードグリッドのセル(ItemsWrapGrid.ItemWidth/ItemHeight)の既定値(正方形)。
    /// コレクション表示(複数コンソール混在でAspectRatioがバラバラ)はこの値のまま使う
    /// (以前の固定値ItemWidth="350" ItemHeight="350"と同じ)。</summary>
    private const double DefaultGridItemSize = 350.0;

    /// <summary>カード(Border)自身が持つMargin="8"の合計(左右・上下それぞれ8+8=16)。
    /// セルサイズをカード実寸に合わせる際、この分を上乗せしないとカード同士が隙間なく
    /// 密着してしまう(セル=カードちょうどだと、Marginの逃げ場が無く欠けて見える)。</summary>
    private const double CardOuterMargin = 16.0;

    /// <summary>コンソール単体表示時に、そのコンソールのAspectRatioからカードの実寸を計算するために
    /// 流用する(GameSelectPage.xamlのPage.Resources内、同名コンバータと同じロジック)。</summary>
    private static readonly AspectRatioToCardSizeConverter CardSizeConverter = new();

    /// <summary>現在のItemsWrapGrid.ItemWidth/ItemHeightに適用したい値。既定は正方形。
    /// コンソール単体表示時のみ<see cref="SetGridItemSizeForConsole"/>で変更する。</summary>
    private double _gridItemWidth = DefaultGridItemSize;
    private double _gridItemHeight = DefaultGridItemSize;

    /// <summary>選択中カードの拡大(Composition Scale、1.13倍)によるはみ出し分の見積もり。
    /// GameSelectPage.xamlのGameGridView.Padding(拡大時のクリップ防止用)と同じ値を使う
    /// (この値を変える場合は両方合わせて変更すること)。
    /// <see cref="SetSelectedGameIndex"/>で選択カードを自動スクロールして画面内に収める際、
    /// この余白分もあわせて画面内に入るようスクロール目標を一回り大きくするために使う
    /// (StartBringIntoViewは拡大前のレイアウト境界しか見ないため、素のままだと最小限の
    /// スクロールで止まってしまい、Paddingで確保した余白まで届かないことがある)。</summary>
    private const double CardScaleOverflowMargin = 24.0;

    /// <summary>
    /// 画面右下に重ねている操作ヒントバー(GameSelectPage.xaml、IsOverlay=TrueのHintBar)による、
    /// 追加の下端クリアランス(1920x1080キャンバス座標系での値)。
    ///
    /// ゲームグリッドは画面いっぱい(Grid.RowSpan=3)に広がっており、ヒントバーはその上に
    /// 重なるオーバーレイになっている。GameGridView.Padding下側(84)は最終行がヒントバーの
    /// 下に潜り込まないための余白だが、一覧の途中のカードをBringIntoViewするときは
    /// スクロール量の計算にこの値が効かないため、目標矩形を下方向にだけ余分に広げて
    /// 拡大表示されたカードの下端がヒントバーの陰に隠れないようにする(BringGameIntoView参照)。
    /// 値はヒントバーの見た目上の高さ(約66)に少し余裕を足した目算。
    /// </summary>
    private const double BottomHintBarClearance = 72.0;

    /// <summary>
    /// 画面上部のヘッダー(コンソールロゴ/選択中ゲーム名/検索ボックス)による、追加の上端クリアランス。
    /// ゲームグリッドが画面いっぱいに広がったため、スクロールしたカードはヘッダーの
    /// 背景グラデーション帯の下へ潜り込む。上方向へBringIntoViewするときは、この分だけ
    /// 目標矩形を上へ広げて、拡大表示されたカードがヘッダーに隠れないようにする。
    /// (GameGridView.Padding上側の96と同程度の値。)
    /// </summary>
    private const double TopHeaderClearance = 84.0;

    /// <summary>現在選択中のRomFileへの参照。SetSelectedGameIndexで「前回選択していたアイテム」を
    /// _selectedGameIndexだけで特定すると、ソート(Games.Move)でインデックスと中身の対応が
    /// ずれた際に、無関係のアイテムを誤って選択解除してしまい、本来解除すべきだった元の
    /// フォーカスアイテムが選択状態(拡大表示)のまま残ってしまう。そのためオブジェクト参照を
    /// 正として選択解除の対象を特定する。</summary>
    private RomFile? _selectedGame;
    private int _columns = 4;

    /// <summary>
    /// ゲーム開始時(<see cref="StartGame"/>)に選択していたRomFile.Noを、GamePlayPageから
    /// Frame.GoBackで戻ってきた際にフォーカスを復元するために一時的に覚えておく場所。
    ///
    /// GameSelectPageはNavigationCacheModeを既定(Disabled)のままにしているため、
    /// Frame.GoBackのたびに毎回“新しいページインスタンス”が生成し直される
    /// (コンストラクタから再実行される)。そのため、普通のインスタンスフィールドでは
    /// 「ゲームを開始する直前のインスタンス」から「戻ってきた後の新しいインスタンス」まで
    /// 値を持ち越せない。static フィールドにすることで、インスタンスをまたいで
    /// 一時的に値を橋渡ししている。
    ///
    /// 消費(参照して選択インデックスへ反映)は<see cref="ApplyFilterAndSort"/>側で
    /// 一覧を読み込み直した直後に一度だけ行い、使ったら必ずnullへ戻す。検索文字列を
    /// 変えた時などの他の経路でApplyFilterAndSortが呼ばれた場合にこの値を誤って
    /// 使い回してしまわないようにするため(その場合は従来通り常に先頭(index 0)を選ぶ)。
    /// </summary>
    private static int? _pendingRestoreGameNo;

    /// <summary>複数枚をまとめた直後、一覧を読み直したときにフォーカスするゲームのNo(1回使ったらクリア)。</summary>
    private int? _pendingFocusNo;

    /// <summary>ゲーム開始時の一覧の表示条件(どのコンソール/コレクションか、ソート方法、検索文字列)。
    /// ゲームから戻ってきたとき、同じコンソール/コレクションなら同じ条件で表示するために、
    /// _pendingRestoreGameNoと同じ理由でstaticに持つ。コンソール選択画面から入り直したときは使わない。</summary>
    private static (string Key, SortMode Sort, string Search)? _pendingRestoreView;

    /// <summary>DBから読み込んだ、コンソール/コレクション単位の全ゲーム(検索フィルタ前)。
    /// <see cref="Games"/>はここから検索フィルタ+現在のソート方法(<see cref="_currentSortMode"/>)を
    /// 適用した結果だけを持つ(<see cref="ApplyFilterAndSort"/>参照)。</summary>
    private List<RomFile> _allGames = new();

    // アーケード(FBNeo/MAME 2003-Plus)のクローン整理。一覧から隠すもの(クローン・BIOS)、
    // 親→選べるバージョン(親が先頭)、クローン→親。クローン表が無い/「すべて表示」のときは空。
    private HashSet<RomFile> _hiddenGames = new();
    private Dictionary<RomFile, List<RomFile>> _variantsOf = new();
    private Dictionary<RomFile, RomFile> _parentOf = new();

    /// <summary>現在選択中のソート方法。コンソール/コレクションへ遷移するたびに既定の
    /// あいうえお順へ戻す(2枚目画像の「お好きな順」は廃止したため、既定=先頭の並び順)。</summary>
    private SortMode _currentSortMode = SortMode.AlphabeticalKana;

    /// <summary>選択中ゲーム名(ヘッダー左上)のスライドインアニメーションの時間。</summary>
    private const double GameNameSlideInMilliseconds = 240;

    /// <summary>選択中ゲーム名が画面外(左)から現れてくる距離(キャンバス座標)。</summary>
    private const float GameNameSlideDistance = 40f;

    /// <summary>GameGridViewのテンプレート内部にあるScrollViewer(L/Rのページ送りで直接
    /// スクロールさせるために使う)。GameGridView.Loadedで一度だけ探して保持する。</summary>
    private ScrollViewer? _gridScrollViewer;

    /// <summary>ページ送り(ChangeView)で指定した移動先のスクロール位置。スクロールアニメーション中に
    /// 続けてL/Rが押された場合、アニメーション途中の現在位置ではなくこの値を起点にすることで、
    /// 連打しても1回あたり正確に1画面分ずつ進むようにする。スクロール完了で null に戻す。</summary>
    private double? _scrollTarget;

    /// <summary>ページ送りのスクロールが終わった後で、選択中カードが(拡大分も含めて)画面内に
    /// 収まっているかを確認し直すためのフラグ(<see cref="GridScrollViewer_ViewChanged"/>参照)。</summary>
    private bool _bringSelectedIntoViewAfterScroll;

    // ---- L/R押しっぱなし高速スクロール(詳細はStartHoldScroll/HoldTimer_Tick参照) ----

    /// <summary>押し始めてから連続スクロールに切り替わるまでの待ち時間。これより短く離せば
    /// 押した瞬間の1画面分のページ送りだけになる。</summary>
    private const double HoldStartDelaySeconds = 0.35;

    /// <summary>連続スクロール開始時の速さ(GridView内の座標で1秒あたりのピクセル数。
    /// カード1行はおよそ360前後なので、約5行/秒)。</summary>
    private const double HoldStartSpeed = 1800;

    /// <summary>押し続けた際の加速度(1秒あたりに増える速さ)。</summary>
    private const double HoldAcceleration = 6000;

    /// <summary>速さの上限(約40行/秒)。</summary>
    private const double HoldMaxSpeed = 14000;

    /// <summary>タイマーの間隔(おおよそ60fps)。</summary>
    private static readonly TimeSpan HoldTickInterval = TimeSpan.FromMilliseconds(16);

    private DispatcherQueueTimer? _holdTimer;
    private readonly Stopwatch _holdClock = new();

    /// <summary>押しっぱなし中の方向(+1=R/下へ、-1=L/上へ)。</summary>
    private int _holdDirection;

    /// <summary>押しっぱなし判定に使う論理ボタン(LeftShoulder/RightShoulder)。</summary>
    private GamepadButton _holdButton;

    /// <summary>待ち時間を過ぎて連続スクロール中かどうか。</summary>
    private bool _holdScrolling;

    /// <summary>連続スクロールで指定しているスクロール位置。ChangeView直後のVerticalOffsetは
    /// まだ古い値のことがあるため、読み戻さずにこの値を積算していく。</summary>
    private double _holdOffset;

    /// <summary>連続スクロール開始時のスクロール位置と選択インデックス。スクロールした行数ぶん
    /// 選択を動かす計算の起点(選択カードを画面上の同じ段に留めるため)。</summary>
    private double _holdStartOffset;
    private int _holdStartIndex;

    private double _holdScrollStartTime;
    private double _holdLastTickTime;

    private bool IsHoldActive => _holdTimer?.IsRunning == true;

    public GameSelectPage()
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
        Loaded += (_, _) => RecalculateColumns();
        SizeChanged += (_, _) => RecalculateColumns();

        // 選択中ゲーム名(ヘッダー左上)のVisualでTranslationをアニメーションさせるための事前設定
        // (SortOverlay.SortPanelと同じ理由。Offsetはレイアウトのたびフレームワーク側が
        // 書き換えるため使わない)。
        ElementCompositionPreview.SetIsTranslationEnabled(SelectedGameNameText, true);

        // OnNavigatedTo時点ではGridView.ItemsPanelRoot(実体のItemsWrapGrid)がまだ
        // テンプレート未適用でnullのことがあるため、GameGridView自身のLoadedでも
        // 保留中のセルサイズ(_gridItemWidth/_gridItemHeight)を再適用しておく。
        GameGridView.Loaded += (_, _) =>
        {
            ApplyPendingGridItemSize();
            AttachGridScrollViewer();
        };

        // 詳細/ソートオーバーレイ(共通UserControl)は、Bボタン等で自分自身を閉じたときに
        // Closedを発火する。ここで_modeをグリッド操作へ戻す。
        DetailOverlay.Closed += (_, _) => { _mode = Mode.Grid; EnsureSearchBoxFocus(); };

        // 詳細画面の「ゲーム情報の編集」で保存したら、ヘッダーの名前と画像も更新する。
        DetailOverlay.GameEdited += (_, game) =>
        {
            if (ReferenceEquals(game, _selectedGame))
            {
                SelectedGameNameText.Text = game.DisplayTitle;
                UpdateSelectedGameThumb(game);
            }
        };

        // 複数枚(ディスク)をまとめた/解除した: 一覧のゲームが増減するので、一覧を読み直す。
        DetailOverlay.DiscSetChanged += (_, _) =>
        {
            _pendingFocusNo = DetailOverlay.DiscSetFocusNo;
            if (!string.IsNullOrEmpty(_console.Id)) _ = LoadGamesForConsoleAsync(_console.Id);
            else if (_collectionInfo is not null) _ = LoadGamesForCollectionAsync(_collectionInfo);
        };

        // アプリ設定「ゲーム起動方法」: 詳細画面から起動する場合は、詳細内の「起動」で開始し、
        // Xボタンの「詳細」の案内は出さない。
        _launchViaDetail = AppPreferencesStore.Load().LaunchViaDetail;
        DetailOverlay.LaunchRequested += (_, _) => StartGame(DetailOverlay.CoverElement);
        // アーケードのクローン: 詳細画面で「バージョン」を選べるようにする(代表+クローン)。
        DetailOverlay.VariantsResolver = game =>
            _variantsOf.TryGetValue(game, out List<RomFile>? list) ? (IReadOnlyList<RomFile>)list : Array.Empty<RomFile>();
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
        if (_launchViaDetail && GameHintBar.Hints.FirstOrDefault(h => h.Key == "X") is { } xHint)
            GameHintBar.Hints.Remove(xHint);
        SortOverlay.Closed += (_, _) => { _mode = Mode.Grid; EnsureSearchBoxFocus(); };
        SortOverlay.SortRequested += SortOverlay_SortRequested;

        // マウス/タッチ用のボタン(ゲームグリッドを操作できる状態のときだけ受け付ける)。
        TouchNav.BackRequested += (_, _) => { if (CanUseTouchNav()) Frame.GoBack(); };
        TouchNav.ConsoleRequested += (_, _) => { if (CanUseTouchNav()) EmulatorLauncher.Controls.TouchNavButtons.GoBackToConsole(Frame); };
        TouchNav.SortRequested += (_, _) => { if (CanUseTouchNav()) ShowSort(); };
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopHoldScroll(bringIntoView: false);
        StopYearJump(finalize: false);
        _searchFocusTimer?.Stop();
        if (AppServices.MainWindow is { } window)
        {
            window.AppActiveStateChanged -= OnAppActiveStateChanged;
        }
    }

    // ------------------------------------------------------------------
    // 検索ボックスへのフォーカス
    // ------------------------------------------------------------------
    // 画面を開いたとき・詳細/ソートを閉じたとき・ゲームから戻ってウィンドウが再びアクティブに
    // なったときに、検索ボックスへフォーカスを戻す。Focus()は1回呼ぶだけだと、レイアウト確定前や
    // ウィンドウが非アクティブの間は失敗する(falseが返る/すぐ他へ奪われる)ことがあるので、
    // 実際にフォーカスが検索ボックスへ移るまで、短い間隔で最大約1秒リトライする。

    private DispatcherQueueTimer? _searchFocusTimer;
    private int _searchFocusAttempts;
    private const int SearchFocusMaxAttempts = 20;

    private void EnsureSearchBoxFocus()
    {
        _searchFocusAttempts = 0;
        if (_searchFocusTimer is null)
        {
            _searchFocusTimer = DispatcherQueue.CreateTimer();
            _searchFocusTimer.Interval = TimeSpan.FromMilliseconds(50);
            _searchFocusTimer.IsRepeating = true;
            _searchFocusTimer.Tick += (_, _) => TryFocusSearchBox();
        }
        _searchFocusTimer.Start();
    }

    private void TryFocusSearchBox()
    {
        // ゲームパッドで詳細/ソートを開いている間は、検索ボックスへ移さない。
        bool overlayOpen = DetailOverlay.IsOpen || SortOverlay.IsOpen;
        bool done = false;

        if (!overlayOpen && SearchTextBox.XamlRoot is { } root)
        {
            SearchTextBox.Focus(FocusState.Programmatic);
            done = ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root), SearchTextBox);
        }
        else if (overlayOpen)
        {
            done = true; // 今は移さない。閉じたとき(Closed)にもう一度呼ぶ。
        }

        if (done || ++_searchFocusAttempts >= SearchFocusMaxAttempts)
        {
            _searchFocusTimer?.Stop();
        }
    }

    private void OnAppActiveStateChanged(bool isActive)
    {
        if (!isActive) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_mode == Mode.Grid) EnsureSearchBoxFocus();
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        TouchNav.RefreshConsoleButton(Frame);

        // バックスタックの直前（最新）が GamePlayPage の場合、履歴から取り除く
        if (Frame != null && Frame.CanGoBack)
        {
            var lastEntry = Frame.BackStack.LastOrDefault();
            if (lastEntry != null && lastEntry.SourcePageType == typeof(GamePlayPage))
            {
                Frame.BackStack.Remove(lastEntry);
            }
        }

        // コンソール/コレクションが切り替わるたびに、検索文字列とソート方法を既定
        // (フィルタなし・あいうえお順)へ戻す。_allGamesを先に空にしてから検索ボックスを
        // クリアすることで、TextChanged経由のApplyFilterAndSortが前のコンソールのデータを
        // 一瞬でも表示してしまわないようにしている。
        _allGames = new List<RomFile>();
        _currentSortMode = SortMode.AlphabeticalKana;
        SelectedGameNameText.Text = string.Empty;
        UpdateSelectedGameThumb(null);
        UpdateSelectedGameBadges(null);
        if (SearchTextBox.Text.Length > 0)
        {
            SearchTextBox.Text = string.Empty;
        }

        // ゲームから戻ってきたとき(同じコンソール/コレクション)だけ、前回のソート方法と検索文字列を復元する。
        // コンソール選択画面から入ったとき(_pendingRestoreGameNoなし)は、従来どおり既定で表示する。
        // 一覧の読み込み(下のLoadGames...)が先に終わるとソートが既定のまま適用されてしまうため、必ず読み込みより前に行う。
        SortMode? restoredSort = null;
        if (_pendingRestoreView is { } view)
        {
            _pendingRestoreView = null;
            string navKey = e.Parameter switch
            {
                ConsoleInfo nc => nc.Id,
                RomCollectionInfo ncol => "collection:" + ncol.Name,
                _ => string.Empty,
            };
            if (_pendingRestoreGameNo is not null && view.Key == navKey)
            {
                _currentSortMode = view.Sort;
                restoredSort = view.Sort;
                if (SearchTextBox.Text != view.Search) SearchTextBox.Text = view.Search;
            }
        }

        // 画面を開いたときは、右上の検索ボックスへフォーカスし、すぐ入力できる状態を既定とする。
        // (ゲームパッドの操作はXAMLのフォーカスとは無関係にポーリングで受けているため影響しない。)
        // レイアウト確定後でないとFocusが効かないことがあるため、UIスレッドのキューへ積む。
        // 1回きりだと、レイアウト確定前・ウィンドウが非アクティブ・他の要素がフォーカスを取った直後などで
        // 失敗することがあるため、実際にフォーカスが移るまで数回リトライする(EnsureSearchBoxFocus)。
        EnsureSearchBoxFocus();
        if (AppServices.MainWindow is { } window)
        {
            window.AppActiveStateChanged -= OnAppActiveStateChanged;
            window.AppActiveStateChanged += OnAppActiveStateChanged;
        }

        if (e.Parameter is ConsoleInfo console)
        {
            _console = console;
            _collectionInfo = null;
            //ConsoleNameText.Text = console.Name;

            // Launchers.jsonで"biosBoot": trueのコア(PS1/PS2/DC等)を使うコンソールだけ、
            // 「本体を起動(BIOS)」(Menuボタン)の案内を出す。
            _biosBootAvailable = SampleData.ResolveLauncher(console.Id, console.LauncherName)?.BiosBoot == true;
            if (GameHintBar.Hints.FirstOrDefault(h => h.Key == "+") is { } oldBiosHint)
                GameHintBar.Hints.Remove(oldBiosHint);
            if (_biosBootAvailable)
                GameHintBar.Hints.Insert(0, new HintDefinition { Key = "+", Label = "BIOS起動" });

            // コンソール単体表示: 表示される全カードのAspectRatioが同じ値になるため、
            // グリッドのセルサイズをそのコンソールのカード実寸に合わせて詰める。
            SetGridItemSizeForConsole(GetConsoleAspectRatio(console.Id));

            ReplaceGames(new ObservableCollection<RomFile>());
            _ = LoadGamesForConsoleAsync(console.Id);
        }
        else if (e.Parameter is RomCollectionInfo collection)
        {
            // コレクションはコンソールをまたぐため、実体としての単一ConsoleInfoは持たない。
            // ゲーム開始時(StartGame→GamePlayPage)は各RomFile.ConsoleIdから個別にコンソールを
            // 逆引きするため、ここでは表示専用の空のConsoleInfoを保持するだけで問題ない。
            _console = new ConsoleInfo { Id = string.Empty, Name = collection.Name };
            _collectionInfo = collection;

            // コレクション表示: 複数コンソールが混在しAspectRatioがバラバラなため、
            // セルサイズはこれ以上詰めようがない。既定の正方形に戻す。
            SetGridItemSizeForConsole(null);

            ReplaceGames(new ObservableCollection<RomFile>());
            _ = LoadGamesForCollectionAsync(collection);
        }

        SortOverlay.Reset();

        // 前回のソート方法を、ソートオーバーレイの選択にも反映する(Resetの後に行う)。
        if (restoredSort is { } sort) SortOverlay.SelectMode(sort);
    }

    /// <summary>いま表示しているコンソール/コレクションを区別する文字列。</summary>
    private string CurrentViewKey() => _console is { Id.Length: > 0 } c ? c.Id : "collection:" + (_console?.Name ?? string.Empty);

    /// <summary>
    /// 指定コンソールに紐づくRomFileをDB(RomFileRepository)から非同期に読み込み、
    /// 読み込み終わったタイミングでGamesとGameGridViewへ反映する。
    /// OnNavigatedTo(戻り値void)から呼ばれるため、ここは「発火して待たない」形にしている
    /// (WinUI3のUIスレッドにはDispatcherQueue経由のSynchronizationContextがあるため、
    /// await後もUIスレッドへ自動的に戻ってくる)。
    /// </summary>
    private async Task LoadGamesForConsoleAsync(string consoleId)
    {
        // ゲーム終了から一覧が出るまでの時間の内訳を、動作ログで追えるようにする。
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        EmulatorLauncher.Services.AppLog.Info("List", $"ゲーム一覧の読み込み開始: {consoleId}");
        IReadOnlyList<RomFile> files;
        try
        {
            files = await GameLibraryFacade.GetFilesByConsoleIdAsync(consoleId);
            // IconPathはスキャン時にファイル名だけ(CRC-32ベース。例: "017FA7E9.png")が保存されて
            // いるため、実際にImage.Sourceへ使えるフルパスへその場で変換する(DBへは書き戻さない)。
            await GameLibraryFacade.ResolveIconPathsAsync(files);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameSelectPage] RomFileの読み込みに失敗しました: {ex.Message}");
            files = Array.Empty<RomFile>();
        }

        long dbMs = loadWatch.ElapsedMilliseconds;
        GameListHelper.ApplyAspectRatios(files);
        _allGames = files.ToList();
        BuildCloneGroups(consoleId);
        ApplyFilterAndSort();
        EmulatorLauncher.Services.AppLog.Info("List", $"ゲーム一覧を表示しました: {consoleId} / {files.Count}本 / DB読み込み {dbMs} ms・合計 {loadWatch.ElapsedMilliseconds} ms");
    }

    /// <summary>指定コンソールIDのAspectRatioをConsoles.json由来のSampleData.GetConsoles()から
    /// 引く(GameListHelper.ApplyAspectRatiosと同じ取得元)。見つからなければnull。</summary>
    private static double? GetConsoleAspectRatio(string consoleId)
    {
        return SampleData.GetConsoles()
            .FirstOrDefault(c => c.Id == consoleId)?.AspectRatio;
    }

    /// <summary>
    /// コレクション(RomCollectionInfo.GameIds、コンソールをまたぐRomFile.Noの一覧)から
    /// 実際のRomFileをDB経由でまとめて読み込む。LoadGamesForConsoleAsyncと同じ考え方。
    /// </summary>
    private async Task LoadGamesForCollectionAsync(RomCollectionInfo collection)
    {
        IReadOnlyList<RomFile> files;
        try
        {
            files = await GameLibraryFacade.GetFilesByNosAsync(collection.GameIds);
            await GameLibraryFacade.ResolveIconPathsAsync(files);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameSelectPage] コレクションのRomFile読み込みに失敗しました: {ex.Message}");
            files = Array.Empty<RomFile>();
        }

        GameListHelper.ApplyAspectRatios(files);
        _allGames = files.ToList();
        BuildCloneGroups(string.Empty);
        ApplyFilterAndSort();
    }

    /// <summary>
    /// アーケード(FBNeo/MAME 2003-Plus)のクローンを親にまとめる。クローンの親が同じコンソールに
    /// 実在するときだけ、クローンを一覧から隠す(親が無いクローンは、そのまま出す)。BIOS等も隠す。
    /// 設定「アーケードのクローン」が「すべて表示」のときは、BIOS等だけを隠してまとめない。
    /// </summary>
    private void BuildCloneGroups(string consoleId)
    {
        _hiddenGames = new HashSet<RomFile>();
        _variantsOf = new Dictionary<RomFile, List<RomFile>>();
        _parentOf = new Dictionary<RomFile, RomFile>();
        if (string.IsNullOrEmpty(consoleId) || !EmulatorLauncher.RomLibrary.ArcadeCloneDb.TryGet(consoleId, out var info)) return;

        bool showClones = AppPreferencesStore.Load().ShowArcadeClones;
        var byStem = new Dictionary<string, RomFile>(StringComparer.OrdinalIgnoreCase);
        foreach (RomFile g in _allGames)
        {
            byStem.TryAdd(System.IO.Path.GetFileNameWithoutExtension(g.FileName), g);
        }

        foreach (RomFile g in _allGames)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(g.FileName);
            if (info.Hidden.Contains(stem)) { _hiddenGames.Add(g); continue; }
            if (showClones || !info.Clones.TryGetValue(stem, out string? parentName)) continue;

            // 親をたどる(念のため段数に上限を付ける)。親が実在しなければ、このクローンは隠さない。
            string root = parentName;
            for (int i = 0; i < 4 && info.Clones.TryGetValue(root, out string? up); i++) root = up;
            if (!byStem.TryGetValue(root, out RomFile? parent) || ReferenceEquals(parent, g)) continue;

            _hiddenGames.Add(g);
            _parentOf[g] = parent;
            if (!_variantsOf.TryGetValue(parent, out List<RomFile>? list))
            {
                list = new List<RomFile> { parent };
                _variantsOf[parent] = list;
            }
            list.Add(g);
        }

        foreach (List<RomFile> list in _variantsOf.Values)
        {
            list.Sort(1, list.Count - 1, Comparer<RomFile>.Create((a, b) =>
                string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.CurrentCulture)));
        }
    }

    /// <summary>
    /// <see cref="_allGames"/>から検索ボックスの文字列で絞り込み、現在のソート方法
    /// (<see cref="_currentSortMode"/>、既定はあいうえお順)で並べ替えた結果をGridViewへ反映する。
    /// コンソール/コレクションの読み込み完了時と、検索文字列が変わるたびに呼ばれる
    /// (<see cref="SearchTextBox_TextChanged"/>参照)。
    ///
    /// フィルタで表示件数そのものが変わるため、<see cref="ReorderGamesWithAnimation"/>のような
    /// 「カードが滑って移動する」演出はせず、単純にItemsSourceを差し替える
    /// (並び順だけを変えるY長押しのソートは、引き続き<see cref="SortOverlay_SortRequested"/>側で
    /// アニメーション付きのMoveを使う)。
    /// </summary>
    private void ApplyFilterAndSort()
    {
        IEnumerable<RomFile> query = _hiddenGames.Count == 0
            ? _allGames
            : _allGames.Where(g => !_hiddenGames.Contains(g));

        string search = SearchTextBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(search))
        {
            if (_parentOf.Count == 0)
            {
                query = query.Where(g => g.MatchesSearch(search));
            }
            else
            {
                // 隠れているクローンの名前に一致したら、その親を出す(親ごとに1回)。
                var hits = new HashSet<RomFile>();
                foreach (RomFile g in _allGames)
                {
                    if (!g.MatchesSearch(search)) continue;
                    hits.Add(_parentOf.TryGetValue(g, out RomFile? parent) ? parent : g);
                }
                query = query.Where(hits.Contains);
            }
        }

        List<RomFile> ordered = GameListHelper.Order(query, _currentSortMode);
        ReplaceGames(new ObservableCollection<RomFile>(ordered));

        if (ordered.Count > 0)
        {
            // GamePlayPageから戻ってきた直後(_pendingRestoreGameNoが立っている)なら、
            // 直前に選んでいたゲームへフォーカスを復元する。見つからない場合(ライブラリが
            // 更新されて対象が消えた等)は、従来通り先頭(index 0)へフォールバックする。
            // 一度参照したら必ずクリアする(詳細はフィールドのコメント参照)。
            int restoreIndex = 0;
            if (_pendingFocusNo is int focusNo)
            {
                int focusIndex = ordered.FindIndex(g => g.No == focusNo);
                if (focusIndex >= 0) restoreIndex = focusIndex;
                _pendingFocusNo = null;
            }
            else if (_pendingRestoreGameNo is int pendingNo)
            {
                int foundIndex = ordered.FindIndex(g => g.No == pendingNo);
                if (foundIndex >= 0)
                {
                    restoreIndex = foundIndex;
                    PlayReturnCoverAnimation(ordered[foundIndex]);
                }
                _pendingRestoreGameNo = null;
            }

            // 選択状態(拡大表示・ヘッダーのゲーム名)自体はここで即座に反映するが、画面内への
            // スクロール(BringGameIntoView)はあえて行わない。直前のReplaceGamesで
            // GameGridView.ItemsSourceを差し替えた直後は、GridView(仮想化パネル)がまだ
            // 新しい内容へレイアウトを反映し終えていないため、この場でScrollIntoViewを呼んでも
            // 正しい位置までスクロールされないことがある(復元先が一覧の下の方にあり、
            // 最初の画面内に収まらない場合に特に顕著。選択自体は正しく飛んでいるのに
            // 画面上は先頭付近のままに見える、という症状になる)。
            // そのため、スクロールだけは1ティック(DispatcherQueueのLow優先度)遅らせて、
            // レイアウトが落ち着いてから行う。
            SetSelectedGameIndex(restoreIndex, bringIntoView: false);

            RomFile gameToBringIntoView = ordered[restoreIndex];
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                BringGameIntoView(gameToBringIntoView);
            });
        }
        else
        {
            // 検索結果が0件: 選択できるカードが無いのでヘッダーの選択中ゲーム名も空にしておく。
            SelectedGameNameText.Text = string.Empty;
            UpdateSelectedGameThumb(null);
            UpdateSelectedGameBadges(null);
        }
    }

    /// <summary>検索ボックスの入力が変わるたびに呼ばれ、タイトルの部分一致でフィルタし直す。</summary>
    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilterAndSort();
    }

    /// <summary>Gamesのインスタンス自体を丸ごと差し替える(新しいコンソール/コレクションへの
    /// 遷移時のみ使う。ソート時は<see cref="ReorderGamesWithAnimation"/>を使うこと)。</summary>
    private void ReplaceGames(ObservableCollection<RomFile> games)
    {
        Games = games;
        GameGridView.ItemsSource = null;
        GameGridView.ItemsSource = Games;
        _selectedGameIndex = -1;
        _selectedGame = null;
        ApplyYearGutterMode();
    }

    /// <summary>
    /// ゲームカードのIconPathが空でなくても、指定した画像を読み込めなかった場合はこのイベントが
    /// 発生する。ImageだけCollapsedにして、下に敷いてあるフォールバック表示
    /// (アクセントカラー背景+グラデーション+タイトル文字)をそのまま見せる
    /// (ConsoleSelectPage.CollectionIcon_ImageFailedと同じ考え方)。
    /// </summary>
    private void GameIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// コンソール単体表示(aspectRatioが非nullかつ正の値)の場合のみ、そのコンソールの
    /// カード実寸(AspectRatioToCardSizeConverterと同じ計算)+カード自身のMargin分へ
    /// _gridItemWidth/_gridItemHeightを合わせる。コレクション表示(aspectRatio=null)は
    /// 既定の正方形へ戻す。
    ///
    /// 実際のItemsWrapGridインスタンスへの反映は<see cref="ApplyPendingGridItemSize"/>が行う
    /// (このメソッド自身はItemsPanelRootのnullチェックをせず、値の決定だけを担当する)。
    /// </summary>
    private void SetGridItemSizeForConsole(double? aspectRatio)
    {
        if (aspectRatio is double ratio && ratio > 0)
        {
            double width = (double)CardSizeConverter.Convert(ratio, typeof(double), "Width", "");
            double height = (double)CardSizeConverter.Convert(ratio, typeof(double), "Height", "");
            _gridItemWidth = width + CardOuterMargin;
            _gridItemHeight = height + CardOuterMargin;
        }
        else
        {
            _gridItemWidth = DefaultGridItemSize;
            _gridItemHeight = DefaultGridItemSize;
        }

        ApplyPendingGridItemSize();
    }

    /// <summary>_gridItemWidth/_gridItemHeightを、実際のItemsWrapGridインスタンス
    /// (GridView.ItemsPanelRoot)へ反映したうえで、列数の再計算とパネルの中央寄せ
    /// (<see cref="RecalculateColumns"/>)を行う。テンプレート未適用でItemsPanelRootがまだ
    /// nullの場合はItemWidth/ItemHeightの反映だけスキップする(その場合はGameGridView.Loadedで
    /// 改めて呼ばれる。コンストラクタ参照)。</summary>
    private void ApplyPendingGridItemSize()
    {
        if (GameGridView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
        {
            wrapGrid.ItemWidth = _gridItemWidth;
            wrapGrid.ItemHeight = _gridItemHeight;
        }

        RecalculateColumns();
    }

    private void RecalculateColumns()
    {
        double itemWidth = _gridItemWidth; // ItemsWrapGridの実際のItemWidthと合わせる(既定は正方形350)

        // GameGridView.Padding(拡大時のクリップ防止用に追加)の左右分は、ItemsWrapGridが
        // 実際に使える幅には含まれない。ここで差し引かないと、_columns(延いてはCenterGridPanelが
        // 設定するWidth)がItemsWrapGrid自身の内部の折り返し判定とズレてしまう。
        UpdateYearGutterPadding();

        double width = GameGridView.ActualWidth - GameGridView.Padding.Left - GameGridView.Padding.Right;
        _columns = Math.Max(1, (int)(width / itemWidth));
        _yearGutterMargin = GameGridView.Padding.Left + (width - _columns * itemWidth) / 2;

        CenterGridPanel();
        RebuildYearLabels();
        UpdateYearGutter();
    }

    /// <summary>
    /// GridView(ItemsWrapGrid)は既定で利用可能な幅いっぱいに広がり、そこへ左詰めでカードを
    /// 並べる。そのため列数がぴったり収まらない場合、余りのスペースが右側だけに寄ってしまう
    /// (例: SFCのように縦長カードで列数が多くなる場合)。
    ///
    /// それを避けるため、実際に並ぶ列数(_columns)ぶんのカード幅だけをItemsWrapGrid自身の
    /// Widthとして明示的に指定し、HorizontalAlignment="Center"でパネル自体を中央寄せする。
    /// こうすると、余りのスペースはパネルの左右に均等に分かれる
    /// (ItemsWrapGrid自身の内部の折り返し計算は、このWidthちょうどに_columns列が収まる値
    /// なので、指定してもレイアウトの崩れは起きない)。
    /// </summary>
    private void CenterGridPanel()
    {
        if (GameGridView.ItemsPanelRoot is not ItemsWrapGrid wrapGrid)
        {
            return;
        }

        wrapGrid.HorizontalAlignment = HorizontalAlignment.Center;
        wrapGrid.Width = _columns * _gridItemWidth;
    }

}
