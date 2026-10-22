using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Numerics;
using System.Threading.Tasks;

namespace EmulatorLauncher.Views;

public sealed partial class ConsoleSelectPage : Page, IControllerPage, EmulatorLauncher.Controls.IHorizontalWheelPage
{
    private enum ViewMode { Consoles, Collections }
    private enum FocusArea { Items, SystemIcons }

    public List<ConsoleInfo> Consoles { get; } = new();
    public List<RomCollectionInfo> Collections { get; } = new();

    private ViewMode _viewMode = ViewMode.Consoles;
    private FocusArea _focus = FocusArea.Items;

    private int _selectedIndex;
    private int _systemIconIndex;

    // ---- コンソール一覧の小サイズ表示(2段×5個のページ表示。△で大サイズと切り替え) ----
    private const int CompactColumns = 5;
    private const int CompactRows = 2;
    private const int CompactPerPage = CompactColumns * CompactRows;
    private bool _compact;
    private int _compactPage = -1;
    private List<ConsoleInfo> _compactItems = new();
    private int _compactSlide;   // 次にページが変わるときの、スライドの向き(+1=次ページ/-1=前ページ/0=位置から判断)

    // ページの点(現在のページは細長いカプセル)
    private const double DotSize = 18;
    private const double DotSpacing = 60;
    private const double PillWidth = 56;
    private const double DotsHeight = 44;   // 点の並びの高さ(タッチで押せる大きさ)
    private int _dotPages;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _dotPill;
    private Storyboard? _pillStoryboard;
    private bool _initialized;

    /// <summary>一覧が横一列のあいだ(大サイズのコンソール一覧、コレクション一覧)は、ホイールの上下を左右の移動にする。小サイズ(2段)は上下のまま。</summary>
    public bool WheelIsHorizontal => _focus == FocusArea.Items && (_viewMode == ViewMode.Collections || !_compact);

    /// <summary>システムアイコンバーのボタン数(操作/設定/最大化/終了の4個)。
    /// 添字の範囲チェック(SetSystemIconIndexのClamp)に使う。</summary>
    private const int SystemIconCount = 5;

    public ConsoleSelectPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = NavigationCacheMode.Required;
        ConsoleCompactRepeater.ElementPrepared += CompactRepeater_ElementPrepared;

        // 小サイズ表示(2段×5個)は、左右へドラッグ(マウス/タッチ)すると、離したときにページが切り替わる。
        // カードがクリックを処理済みにしても受け取れるよう、handledEventsToo で付ける。
        ConsoleCompactPanel.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(CompactSwipe_Pressed), true);
        ConsoleCompactPanel.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(CompactSwipe_Moved), true);
        ConsoleCompactPanel.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(CompactSwipe_Released), true);
        ConsoleCompactPanel.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(CompactSwipe_Ended), true);
        ConsoleCompactPanel.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(CompactSwipe_Ended), true);

        // 横スクロールの一覧は、ScrollViewerが縦のホイールを横スクロールに使ってしまう。ホイールは上下(Shift+ホイールで左右)の
        // 操作に統一するため、一覧の中身でホイールを先に受け取ってMainWindowの変換へ回す。
        ConsoleItemsControl.PointerWheelChanged += (_, e) => AppServices.MainWindow?.HandleLauncherWheel(e);
        CollectionItemsControl.PointerWheelChanged += (_, e) => AppServices.MainWindow?.HandleLauncherWheel(e);

        // マウスが一覧の上にあるときだけ、左右の端にページ移動の矢印を出す。
        // Viewbox内の要素は、コンストラクターの時点ではParentがnullのことがある(ログで確認)。画面が読み込まれてから取り付ける。
        Loaded += (_, _) =>
        {
            if (_scrollArrowsInstalled) return;
            _scrollArrowsInstalled = true;
            InstallScrollArrows(ConsoleScrollViewer);
            InstallScrollArrows(CollectionScrollViewer);
        };

        // コレクション編集オーバーレイを閉じた。保存/削除でDBが変わった場合だけ一覧を読み直す
        // (以前はCollectionEditPageから戻ってきた時のOnNavigatedToで読み直していた)。
        // 選択位置はなるべく保つ(削除時は次のカード、新規追加時は「新規追加」カードの位置に
        // 追加されたコレクションが来る)。
        EditOverlay.Closed += (_, changed) =>
        {
            if (changed) _ = LoadCollectionsAsync(keepSelection: true);
        };

        // コンソール編集オーバーレイを閉じた。保存された場合だけ一覧を読み直す
        // (アイコン/デフォルトランチャーはConsoles.jsonから読み直されて反映される)。
        ConsoleEditor.Closed += (_, saved) =>
        {
            if (saved) _ = LoadConsolesAsync();
        };

        Loaded += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateViewModeVisuals();
                UpdateSystemIconHighlight();
                SetSelectedIndex(0);
            });
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _compact = EmulatorLauncher.Emulation.AppPreferencesStore.Load().ConsoleViewCompact;
        UpdateViewModeVisuals();
        StartClock();
        _ = LoadCollectionsAsync();
        _ = LoadConsolesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _clock?.Stop();
    }

    // ---- 左下の時計(HH:mm) ----

    private DispatcherTimer? _clock;

    private void StartClock()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        if (_clock is null)
        {
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (_, _) =>
            {
                string now = DateTime.Now.ToString("HH:mm");
                if (ClockText.Text != now) ClockText.Text = now;
            };
        }
        _clock.Start();
    }

    private async Task LoadConsolesAsync()
    {
        // 再読み込み前に選択中だったコンソールのIdを覚えておき、読み込み後に同じコンソールへ
        // 選択を戻す(タイトル数の再集計のたびに先頭へ選択が飛んでしまうのを防ぐため)。
        string? previousSelectedId = (_viewMode == ViewMode.Consoles
            && _selectedIndex >= 0 && _selectedIndex < Consoles.Count)
            ? Consoles[_selectedIndex].Id
            : null;

        List<ConsoleInfo> allConsoles = SampleData.GetConsoles();

        Dictionary<string, int> counts;
        try
        {
            counts = await GameLibraryFacade.GetFileCountsByConsoleIdAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConsoleSelectPage] タイトル数の取得に失敗しました: {ex.Message}");
            counts = new Dictionary<string, int>();
        }

        var withTitles = new List<ConsoleInfo>();
        foreach (ConsoleInfo console in allConsoles)
        {
            console.GameCount = counts.TryGetValue(console.Id, out int matched) ? matched : 0;
            if (console.GameCount > 0)
            {
                withTitles.Add(console);
            }
        }

        // タイトルが0件のコンソールは表示しない。1件も無いとき(ROMフォルダが未登録など)は、
        // 一覧を空にして、案内の文を出す(設定の「ゲームフォルダ管理」から登録してもらう)。
        List<ConsoleInfo> toShow = withTitles;
        if (withTitles.Count == 0)
        {
            EmulatorLauncher.Services.AppLog.Info("Console", "タイトルのあるコンソールがありません(ROMフォルダが未登録の可能性があります)");
        }

        Consoles.Clear();
        foreach (ConsoleInfo console in toShow)
        {
            Consoles.Add(console);
        }

        ConsoleItemsControl.ItemsSource = null;
        ConsoleItemsControl.ItemsSource = Consoles;
        _compactPage = -1;   // 小サイズ表示のページを作り直す

        // 一覧が空のときは、前に選んでいたコンソールのロゴが残らないようにして、案内を出す。
        if (Consoles.Count == 0)
        {
            SelectedLogoImage.Source = null;
            SelectedLogoImage.Opacity = 0;
        }
        _consolesLoaded = true;
        UpdateEmptyGuide();

        if (_viewMode == ViewMode.Consoles)
        {
            int restoreIndex = previousSelectedId is null
                ? 0
                : Math.Max(0, Consoles.FindIndex(c => c.Id == previousSelectedId));
            SetSelectedIndex(restoreIndex);
        }
    }

    /// <param name="keepSelection">trueなら読み込み後も選択位置(インデックス)をなるべく保つ
    /// (EditOverlayを閉じた直後: 削除/新規追加で並びが変わる前提の挙動)。
    /// falseなら、読み込み前に選択中だったコレクションのIdを覚えておき、読み込み後に同じ
    /// コレクションへ選択を戻す(ページ復帰時。LoadConsolesAsyncと同じ考え方)。</param>
    private async Task LoadCollectionsAsync(bool keepSelection = false)
    {
        // 再読み込み前に選択中だったコレクションのIdを覚えておく(keepSelection時は対象外)。
        string? previousSelectedId = (_viewMode == ViewMode.Collections
            && !keepSelection
            && _selectedIndex >= 0 && _selectedIndex < Collections.Count)
            ? Collections[_selectedIndex].Id
            : null;

        List<RomCollectionInfo> collections;
        try
        {
            collections = await CollectionLibraryFacade.GetAllAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConsoleSelectPage] コレクション一覧の読み込みに失敗しました: {ex.Message}");
            collections = new List<RomCollectionInfo>();
        }

        Collections.Clear();
        foreach (RomCollectionInfo collection in collections)
        {
            Collections.Add(collection);
        }

        Collections.Add(new RomCollectionInfo { IsAddNewCard = true });

        CollectionItemsControl.ItemsSource = null;
        CollectionItemsControl.ItemsSource = Collections;

        if (_viewMode == ViewMode.Collections)
        {
            int restoreIndex;
            if (keepSelection)
            {
                restoreIndex = Math.Clamp(_selectedIndex, 0, Collections.Count - 1);
            }
            else
            {
                restoreIndex = previousSelectedId is null
                    ? 0
                    : Math.Max(0, Collections.FindIndex(c => c.Id == previousSelectedId));
            }
            SetSelectedIndex(restoreIndex);
        }
    }

    // 「コンソール/コレクション」の切り替えアニメーションの最中は、次の切り替えを受け付けない。
    // L2とR2を同時に押すと、2回続けて切り替わってアニメーションが重なり、
    // 先の切り替えの終了処理が後の切り替えで表示したパネルを隠して、一覧が消えてしまっていた。
    private long _lastViewModeToggleMs;
    private void ToggleViewMode()
    {
        // 連打・L2/R2同時押しの二重実行を防ぐ。タイマーは破棄されると解除されず固まるため、時刻で判定する。
        long now = Environment.TickCount64;
        if (now - _lastViewModeToggleMs < 320) return;
        _lastViewModeToggleMs = now;
        _viewMode = _viewMode == ViewMode.Consoles ? ViewMode.Collections : ViewMode.Consoles;
        SetSelectedIndex(0);
        AnimateViewModeSwitch();
    }

    private void AnimateViewModeSwitch()
    {
        bool consolesActive = _viewMode == ViewMode.Consoles;
        FrameworkElement consolePanel = _compact ? ConsoleCompactPanel : ConsoleScrollViewer;
        FrameworkElement outgoing = consolesActive ? CollectionScrollViewer : consolePanel;
        FrameworkElement incoming = consolesActive ? consolePanel : CollectionScrollViewer;

        UpdateTabLabels();
        UpdateEmptyGuide(); // 「ゲームがまだ登録されていません」の案内は、コンソール一覧のときだけ出す

        // 画面全体が横へ流れるように切り替える(コレクションへは左へ、コンソールへは右へ戻る)。
        int dir = consolesActive ? -1 : 1;
        SlidePanel(outgoing, 0, -dir * 160f, 200);

        var fadeOut = AnimateOpacity(outgoing, 1, 0, 160);
        fadeOut.Completed += (_, _) =>
        {
            // 念のため: このパネルが、いまの表示対象に戻っていたら隠さない。
            bool stillActive = _viewMode == ViewMode.Collections
                ? ReferenceEquals(outgoing, CollectionScrollViewer)
                : ReferenceEquals(outgoing, _compact ? ConsoleCompactPanel : ConsoleScrollViewer);
            if (stillActive)
            {
                fadeOut.Stop();
                outgoing.Opacity = 1;
                return;
            }

            outgoing.Visibility = Visibility.Collapsed;
            fadeOut.Stop();
            outgoing.Opacity = 1;
            ElementCompositionPreview.GetElementVisual(outgoing).Properties.InsertVector3("Translation", Vector3.Zero);
        };

        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;
        SlidePanel(incoming, dir * 160f, 0, 280);
        AnimateOpacity(incoming, 0, 1, 220);

        if (!consolesActive)
        {
            SelectedLogoImage.Source = null;
            SelectedLogoImage.Opacity = 0;
        }
    }

    /// <summary>パネルを横方向へ動かす(Translation.Xのコンポジションアニメーション)。</summary>
    private static void SlidePanel(FrameworkElement target, float from, float to, int milliseconds)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(target, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(target);
        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));
        ScalarKeyFrameAnimation slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0f, from);
        slide.InsertKeyFrame(1f, to, easing);
        slide.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Translation.X", slide);
    }

    private static Storyboard AnimateOpacity(FrameworkElement target, double from, double to, int milliseconds)
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Begin();
        return storyboard;
    }

    /// <summary>コンソールが1つも無い(ゲームフォルダが未登録など)とき、案内の文を出す。</summary>
    private bool _consolesLoaded; // 最初の読み込みが終わるまでは案内を出さない(読み込み中に一瞬出てしまうため)

    private void UpdateEmptyGuide()
    {
        EmptyGuideText.Visibility = _consolesLoaded && _viewMode == ViewMode.Consoles && Consoles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateViewModeVisuals()
    {
        bool consolesActive = _viewMode == ViewMode.Consoles;

        UpdateTabLabels();

        UpdateEmptyGuide();
        ConsoleScrollViewer.Visibility = consolesActive && !_compact ? Visibility.Visible : Visibility.Collapsed;
        ConsoleScrollViewer.Opacity = consolesActive ? 1 : 0;
        ConsoleCompactPanel.Visibility = consolesActive && _compact ? Visibility.Visible : Visibility.Collapsed;
        ConsoleCompactPanel.Opacity = consolesActive ? 1 : 0;
        CollectionScrollViewer.Visibility = consolesActive ? Visibility.Collapsed : Visibility.Visible;
        CollectionScrollViewer.Opacity = consolesActive ? 0 : 1;

        if (!consolesActive)
        {
            SelectedLogoImage.Source = null;
            SelectedLogoImage.Opacity = 0;
        }
    }

    private void UpdateTabLabels()
    {
        bool consolesActive = _viewMode == ViewMode.Consoles;

        SetActiveTab(consolesActive);

        // 小サイズ表示では一覧が小さく余白が多いので、ロゴとタブを上へ詰める。
        ConsoleHeaderGrid.Margin = _compact ? new Thickness(24, 14, 24, 0) : new Thickness(24, 50, 24, 0);
        SelectedLogoImage.Height = _compact ? 120 : 190;
        ConsoleTabBarScale.ScaleX = ConsoleTabBarScale.ScaleY = _compact ? 0.82 : 1.0;
        ApplySystemIconLayout();
        // 小サイズ表示では、ロゴの上の余白と、ロゴ〜コンソール一覧の間の余白が同じくらいになるよう、
        // ロゴだけを少し下へずらす(下の余白をマイナスにして、ヘッダー全体の高さは変えない)。
        SelectedLogoImage.Margin = _compact ? new Thickness(100, 26, 0, -26) : new Thickness(100, 20, 0, 0);

        EditHint.Label = consolesActive ? "コンソール編集" : "コレクション編集";
        // L2/R2は「いまと反対の一覧」へ移る操作なので、行き先に合わせて表示を変える。
        TabSwitchHint.Label = consolesActive ? "コレクション移動" : "コンソール移動";

        // タブの色も、いまの表示の選択中の項目に合わせる。
        if (consolesActive && _selectedIndex >= 0 && _selectedIndex < Consoles.Count)
            ApplyTabAccent(Consoles[_selectedIndex].AccentColorHex);
        else if (!consolesActive && _selectedIndex >= 0 && _selectedIndex < Collections.Count)
            ApplyTabAccent(Collections[_selectedIndex].AccentColorHex);

        // 「表示切替」(△)はコンソール一覧だけの操作なので、コレクション表示中は操作ガイドから外す。
        if (consolesActive)
        {
            if (!ConsoleHintBar.Hints.Contains(SizeHint))
                ConsoleHintBar.Hints.Insert(Math.Min(2, ConsoleHintBar.Hints.Count), SizeHint);
        }
        else
        {
            ConsoleHintBar.Hints.Remove(SizeHint);
        }

        // 「ページ移動」(L/R)は、縮小表示でページが複数あるときだけ表示する。
        bool showPage = consolesActive && _compact && CompactPageCount > 1;
        if (showPage)
        {
            if (!ConsoleHintBar.Hints.Contains(PageHint))
                ConsoleHintBar.Hints.Insert(Math.Min(1, ConsoleHintBar.Hints.Count), PageHint);
        }
        else
        {
            ConsoleHintBar.Hints.Remove(PageHint);
        }

        UpdateTabSwitchHint();
    }

    // ---- 右上タブ: 大きさ(選択中=大/非選択=小)と下線のスライド ----

    private const double TabActiveScale = 42.0 / 32.0;
    private const double TabGap = 28;
    private double _tabCw, _tabCh, _tabLw, _tabLh;   // 各タブの標準サイズ(文字32/アイコン38のとき)
    private double _kConsole = 1, _kCollection = 1;  // 現在の拡大率(1=小〜TabActiveScale=大)
    private DispatcherTimer? _tabTimer;
    private System.Diagnostics.Stopwatch? _tabClock;
    private double _kConsoleFrom, _kCollectionFrom, _kConsoleTo, _kCollectionTo;
    private bool _tabsInitialized;

    private void MeasureTabs()
    {
        if (_tabCw > 0) return;
        foreach (var (grid, isConsole) in new[] { (ConsoleTab, true), (CollectionTab, false) })
        {
            var content = (grid.Children[0] as Viewbox)?.Child as FrameworkElement;
            if (content is null) continue;
            content.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (isConsole) { _tabCw = content.DesiredSize.Width; _tabCh = content.DesiredSize.Height; }
            else { _tabLw = content.DesiredSize.Width; _tabLh = content.DesiredSize.Height; }
        }
    }

    private void SetActiveTab(bool consolesActive)
    {
        MeasureTabs();
        _kConsoleTo = consolesActive ? TabActiveScale : 1;
        _kCollectionTo = consolesActive ? 1 : TabActiveScale;

        if (!_tabsInitialized || _tabCw <= 0)
        {
            _tabsInitialized = _tabCw > 0;
            _kConsole = _kConsoleTo; _kCollection = _kCollectionTo;
            ApplyTabLayout();
            return;
        }

        _kConsoleFrom = _kConsole; _kCollectionFrom = _kCollection;
        _tabClock = System.Diagnostics.Stopwatch.StartNew();
        if (_tabTimer is null)
        {
            _tabTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _tabTimer.Tick += (_, _) =>
            {
                double p = Math.Min(1.0, (_tabClock?.Elapsed.TotalMilliseconds ?? 999) / 260.0);
                double e = 1 - Math.Pow(1 - p, 3); // ease-out
                _kConsole = _kConsoleFrom + (_kConsoleTo - _kConsoleFrom) * e;
                _kCollection = _kCollectionFrom + (_kCollectionTo - _kCollectionFrom) * e;
                ApplyTabLayout();
                if (p >= 1.0) _tabTimer?.Stop();
            };
        }
        _tabTimer.Start();
    }

    private void ApplyTabLayout()
    {
        if (_tabCw <= 0) return;
        double maxH = Math.Max(_tabCh, _tabLh) * TabActiveScale;
        TabRowHost.Height = maxH;

        ConsoleTab.Width = _kConsole * _tabCw; ConsoleTab.Height = _kConsole * _tabCh;
        CollectionTab.Width = _kCollection * _tabLw; CollectionTab.Height = _kCollection * _tabLh;

        double span = TabActiveScale - 1;
        double aC = Math.Clamp((_kConsole - 1) / span, 0, 1);
        double aL = Math.Clamp((_kCollection - 1) / span, 0, 1);
        ConsoleTabIconAccent.Opacity = aC; ConsoleTabIconGray.Opacity = 0.5 * (1 - aC); ConsoleTabText.Opacity = 0.5 + 0.5 * aC;
        CollectionTabIconAccent.Opacity = aL; CollectionTabIconGray.Opacity = 0.5 * (1 - aL); CollectionTabText.Opacity = 0.5 + 0.5 * aL;

        // 下線: コンソールの下(左)からコレクションの下(右)へ、位置と幅を補間してすべらせる。
        double cW = ConsoleTab.Width, lW = CollectionTab.Width;
        TabUnderline.Margin = new Thickness(aL * (cW + TabGap), 0, 0, 0);
        TabUnderline.Width = cW + (lW - cW) * aL;
        TabUnderlineHost.Width = cW + TabGap + lW;
    }

    // ---- 右上タブの色: 選択中のコンソール/コレクションのアクセント色(アイコンと下線) ----

    private SolidColorBrush? _tabAccentBrush;

    private void ApplyTabAccent(string? hex)
    {
        Windows.UI.Color color;
        try
        {
            string h = (hex ?? "").TrimStart('#');
            if (h.Length != 6) return;
            color = Windows.UI.Color.FromArgb(255,
                System.Convert.ToByte(h.Substring(0, 2), 16),
                System.Convert.ToByte(h.Substring(2, 2), 16),
                System.Convert.ToByte(h.Substring(4, 2), 16));
        }
        catch { return; }

        if (_tabAccentBrush is null)
        {
            // 4つの要素で1つのブラシを共有し、色だけをなめらかに変える。
            _tabAccentBrush = new SolidColorBrush(color);
            ConsoleTabIconAccent.Foreground = _tabAccentBrush;
            CollectionTabIconAccent.Foreground = _tabAccentBrush;
            TabUnderline.Fill = _tabAccentBrush;
            return;
        }

        var anim = new ColorAnimation { To = color, Duration = new Duration(TimeSpan.FromMilliseconds(220)), EnableDependentAnimation = true };
        Storyboard.SetTarget(anim, _tabAccentBrush);
        Storyboard.SetTargetProperty(anim, "Color");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    /// <summary>横スクロールの一覧(ScrollViewer)と同じセルに、左右のページ移動の矢印を重ねる。</summary>
    private bool _scrollArrowsInstalled;

    private void InstallScrollArrows(ScrollViewer scroll)
    {
        DependencyObject? parentObject = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(scroll);
        if (parentObject is not Grid parent) return;
        var overlay = new EmulatorLauncher.Controls.ScrollArrowOverlay(scroll, ScrollArrowPage, showWhilePointerUsed: true);
        Grid.SetRow(overlay, Grid.GetRow(scroll));
        Grid.SetColumn(overlay, Grid.GetColumn(scroll));
        Grid.SetRowSpan(overlay, Grid.GetRowSpan(scroll));
        Grid.SetColumnSpan(overlay, Grid.GetColumnSpan(scroll));
        parent.Children.Add(overlay);
    }

    /// <summary>
    /// カードをクリック/タップしたとき。フォーカスされていないカードは、まずフォーカスを移すだけ(誤って開かないように)。
    /// フォーカス中のカードをもう一度押すと決定(Aと同じ)。コンソール/コレクション/小サイズ表示で共通。
    /// </summary>
    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (SystemSettings.IsOpen || ConsoleEditor.IsOpen || EditOverlay.IsOpen) return;
        if (Environment.TickCount64 < _swipeSuppressTapUntilMs) return;   // スワイプの直後
        if (sender is not FrameworkElement card) return;

        // 小サイズ表示(ItemsRepeater)の要素にはDataContextが入らないので、リピーターから番号を取る。
        int index = ConsoleCompactRepeater.GetElementIndex(card);
        // リピーター内の番号は「いま表示中のページの中」の番号なので、全体の番号へ直す(2ページ目以降のため)。
        if (index >= 0) index += _compactPage * CompactPerPage;
        if (index < 0)
        {
            index = card.DataContext switch
            {
                ConsoleInfo c when _viewMode == ViewMode.Consoles => Consoles.IndexOf(c),
                RomCollectionInfo r when _viewMode == ViewMode.Collections => Collections.IndexOf(r),
                _ => -1,
            };
        }
        if (index < 0) return;

        if (_focus == FocusArea.Items && index == _selectedIndex)
        {
            NavigateToSelected();
            return;
        }

        if (_focus == FocusArea.SystemIcons) ExitSystemIconsFocus();
        SetSelectedIndex(index);
    }

    // ---- 右上の「コンソール」「コレクション」のクリック/タップ(L2/R2と同じ切り替え) ----

    private void ConsoleTab_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (SystemSettings.IsOpen || ConsoleEditor.IsOpen || EditOverlay.IsOpen) return;
        if (_viewMode != ViewMode.Consoles) ToggleViewMode();
    }

    private void CollectionTab_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (SystemSettings.IsOpen || ConsoleEditor.IsOpen || EditOverlay.IsOpen) return;
        if (_viewMode != ViewMode.Collections) ToggleViewMode();
    }

    // ---- 小サイズ表示のスワイプ(左右へドラッグ→離したときにページ送り) ----

    private const double SwipeStartDistance = 24;    // この距離(横)を超えたらドラッグとみなす
    private const double SwipePageDistance = 80;     // 離したとき、この距離以上動いていればページを送る
    private uint _swipePointerId;
    private bool _swipeTracking;
    private bool _swiping;
    private Windows.Foundation.Point _swipeStart;
    private long _swipeSuppressTapUntilMs;   // ドラッグの直後に、離した場所のカードが「クリック」されないようにする

    private bool CanSwipeCompact()
        => _viewMode == ViewMode.Consoles && _compact && CompactPageCount > 1
           && !SystemSettings.IsOpen && !ConsoleEditor.IsOpen && !EditOverlay.IsOpen;

    private void CompactSwipe_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanSwipeCompact()) return;
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(ConsoleCompactPanel);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;

        _swipeTracking = true;
        _swiping = false;
        _swipePointerId = e.Pointer.PointerId;
        _swipeStart = point.Position;
    }

    private void CompactSwipe_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_swipeTracking || e.Pointer.PointerId != _swipePointerId) return;

        Windows.Foundation.Point pos = e.GetCurrentPoint(ConsoleCompactPanel).Position;
        if (_swiping) return;

        double dx = pos.X - _swipeStart.X;
        double dy = pos.Y - _swipeStart.Y;
        if (Math.Abs(dy) > SwipeStartDistance && Math.Abs(dy) > Math.Abs(dx))
        {
            _swipeTracking = false;   // 縦の動きはスワイプではない
            return;
        }

        if (Math.Abs(dx) > SwipeStartDistance && Math.Abs(dx) > Math.Abs(dy) * 1.5)
        {
            _swiping = true;
            ConsoleCompactPanel.CapturePointer(e.Pointer);   // 以降の動き/離しはここで受ける(離した場所のカードを押したことにしない)
        }
    }

    private void CompactSwipe_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_swipeTracking || e.Pointer.PointerId != _swipePointerId) return;

        bool swiped = _swiping;
        double dx = e.GetCurrentPoint(ConsoleCompactPanel).Position.X - _swipeStart.X;
        _swipeTracking = false;
        _swiping = false;
        if (!swiped) return;

        e.Handled = true;
        _swipeSuppressTapUntilMs = Environment.TickCount64 + 300;
        ConsoleCompactPanel.ReleasePointerCapture(e.Pointer);

        if (Math.Abs(dx) < SwipePageDistance || !CanSwipeCompact()) return;
        if (_focus == FocusArea.SystemIcons) ExitSystemIconsFocus();
        MoveCompactPage(dx < 0 ? +1 : -1);   // 左へドラッグ=次のページ(L/Rと同じく、端では反対側へ回る)
    }

    private void CompactSwipe_Ended(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _swipePointerId) return;
        _swipeTracking = false;
        _swiping = false;
    }

    /// <summary>小サイズ表示のページの点をクリック/タップしたら、そのページへ移る(選択は同じ位置のカードへ)。</summary>
    private void ConsolePageDots_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (SystemSettings.IsOpen || ConsoleEditor.IsOpen || EditOverlay.IsOpen) return;
        if (_viewMode != ViewMode.Consoles || !_compact || Consoles.Count == 0) return;

        int pages = CompactPageCount;
        if (pages <= 1) return;

        double x = e.GetPosition(ConsolePageDots).X;
        int target = Math.Clamp((int)Math.Round((x - PillWidth / 2) / DotSpacing), 0, pages - 1);
        int current = _selectedIndex / CompactPerPage;
        if (target == current) return;

        if (_focus == FocusArea.SystemIcons) ExitSystemIconsFocus();
        _compactSlide = target > current ? 1 : -1;
        int offset = _selectedIndex % CompactPerPage;
        SetSelectedConsoleIndex(Math.Min(target * CompactPerPage + offset, Consoles.Count - 1));
    }

    /// <summary>矢印を押したとき: 見えている枚数ぶん、選択(フォーカス)を左右へ動かす(端では端まで)。一覧は選択に合わせてスクロールする。</summary>
    private void ScrollArrowPage(int direction)
    {
        if (SystemSettings.IsOpen || ConsoleEditor.IsOpen || EditOverlay.IsOpen) return;
        if (_focus == FocusArea.SystemIcons) ExitSystemIconsFocus();

        bool consoles = _viewMode == ViewMode.Consoles;
        int count = consoles ? Consoles.Count : Collections.Count;
        if (count == 0) return;

        ItemsControl items = consoles ? ConsoleItemsControl : CollectionItemsControl;
        ScrollViewer scroll = consoles ? ConsoleScrollViewer : CollectionScrollViewer;
        double pitch = items.ActualWidth / count;
        int page = pitch > 0 ? Math.Max(1, (int)(scroll.ViewportWidth / pitch)) : 1;
        int target = Math.Clamp(_selectedIndex + direction * page, 0, count - 1);
        if (target == _selectedIndex) return;
        SetSelectedIndex(target);
    }

    private void SetSelectedIndex(int index)
    {
        if (_viewMode == ViewMode.Consoles)
            SetSelectedConsoleIndex(index);
        else
            SetSelectedCollectionIndex(index);
    }

    private readonly EmulatorLauncher.Controls.HintDefinition _biosHint = new() { Key = BiosBoot.HintKey, Label = BiosBoot.HintLabel };

    /// <summary>選んでいるコンソールが本体(BIOS)起動に対応していれば、「本体を起動(BIOS)」(Menuボタン)の案内を出す。</summary>
    private void UpdateBiosHint()
    {
        bool wanted = _viewMode == ViewMode.Consoles && Consoles is { Count: > 0 }
            && _selectedIndex >= 0 && _selectedIndex < Consoles.Count
            && BiosBoot.IsAvailable(Consoles[_selectedIndex]);
        bool shown = ConsoleHintBar.Hints.Contains(_biosHint);
        if (wanted && !shown) ConsoleHintBar.Hints.Insert(0, _biosHint);
        else if (!wanted && shown) ConsoleHintBar.Hints.Remove(_biosHint);

        UpdateTabSwitchHint();
    }

    /// <summary>「L2/R2 コレクション移動」は、BIOS起動と「L/R ページ移動」が同時に出る(縮小表示)ときだけ隠す。
    /// コントローラー2台分の表示が左下にあっても、操作説明と重ならないようにするため。</summary>
    private void UpdateTabSwitchHint()
    {
        bool hide = ConsoleHintBar.Hints.Contains(_biosHint) && ConsoleHintBar.Hints.Contains(PageHint);
        bool shown = ConsoleHintBar.Hints.Contains(TabSwitchHint);
        if (hide && shown) ConsoleHintBar.Hints.Remove(TabSwitchHint);
        else if (!hide && !shown) ConsoleHintBar.Hints.Insert(0, TabSwitchHint);
    }

    private void StartBiosBootOfSelected()
    {
        if (_viewMode != ViewMode.Consoles || Consoles.Count == 0 || _selectedIndex < 0 || _selectedIndex >= Consoles.Count) return;
        BiosBoot.Start(Frame, Consoles[_selectedIndex]);
    }

    private void SetSelectedConsoleIndex(int index)
    {
        if (Consoles == null || Consoles.Count == 0) return;
        index = ((index % Consoles.Count) + Consoles.Count) % Consoles.Count;

        _selectedIndex = index;
        UpdateBiosHint();
        if (_compact) ShowCompactPage(index / CompactPerPage);
        ApplyConsoleHighlight(index);
        ApplyTabAccent(Consoles[index].AccentColorHex);

        if (_focus == FocusArea.Items)
        {
            UpdateSelectedLogo(Consoles[index].LogoPath);
        }

        if (!_compact && ConsoleItemsControl.ContainerFromIndex(index) is FrameworkElement element)
        {
            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        }
    }

    private void SetSelectedCollectionIndex(int index)
    {
        if (Collections == null || Collections.Count == 0) return;
        index = ((index % Collections.Count) + Collections.Count) % Collections.Count;

        _selectedIndex = index;
        UpdateBiosHint();
        ApplyCollectionHighlight(index);
        ApplyTabAccent(Collections[index].AccentColorHex);

        SelectedLogoImage.Source = null;
        SelectedLogoImage.Opacity = 0;

        if (CollectionItemsControl.ContainerFromIndex(index) is FrameworkElement element)
        {
            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        }
    }

    private void RefreshItemHighlight()
    {
        if (_viewMode == ViewMode.Consoles)
            ApplyConsoleHighlight(_selectedIndex);
        else
            ApplyCollectionHighlight(_selectedIndex);
    }

    private void ApplyConsoleHighlight(int index)
    {
        bool itemsFocused = _focus == FocusArea.Items;
        for (int i = 0; i < Consoles.Count; i++)
        {
            Consoles[i].IsSelected = itemsFocused && i == index;
        }

        if (_compact) UpdateCompactScales();
    }

    private void ApplyCollectionHighlight(int index)
    {
        bool itemsFocused = _focus == FocusArea.Items;
        for (int i = 0; i < Collections.Count; i++)
        {
            Collections[i].IsSelected = itemsFocused && i == index;
        }
    }

    private void UpdateSelectedLogo(string logoPath)
    {
        if (!string.IsNullOrEmpty(logoPath))
        {
            try
            {
                BitmapImage bitmap = new BitmapImage();

                bitmap.ImageOpened += (s, e) =>
                {
                    AnimateLogo();
                };

                bitmap.UriSource = new Uri(logoPath);
                SelectedLogoImage.Source = bitmap;

                DispatcherQueue.TryEnqueue(() =>
                {
                    AnimateLogo();
                });
            }
            catch
            {
                SelectedLogoImage.Source = null;
            }
        }
        else
        {
            SelectedLogoImage.Source = null;
        }
    }

    /// <summary>
    /// ロゴのフェードイン+上からのスライド。
    /// 以前はOffsetを(0,-20)→(0,0)へアニメーションしていたが、OffsetはXAMLレイアウトが決めた
    /// 要素の位置そのものなので、終了時にロゴが親の左上(0,0)へ移動してしまい、実行のたびに
    /// 位置がまちまちになっていた。レイアウト位置に対する相対移動であるTranslationを使う。
    /// </summary>
    private void AnimateLogo()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(SelectedLogoImage, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(SelectedLogoImage);
        Compositor compositor = visual.Compositor;

        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.0f, 0.0f),
            new Vector2(0.2f, 1.0f)
        );

        TimeSpan duration = TimeSpan.FromMilliseconds(500);

        ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.Target = "Opacity";
        opacityAnim.Duration = duration;
        opacityAnim.InsertKeyFrame(0.0f, 0.0f);
        opacityAnim.InsertKeyFrame(1.0f, 1.0f, easing);

        Vector3KeyFrameAnimation offsetAnim = compositor.CreateVector3KeyFrameAnimation();
        offsetAnim.Target = "Translation";
        offsetAnim.Duration = duration;
        offsetAnim.InsertKeyFrame(0.0f, new Vector3(0, -20f, 0));
        offsetAnim.InsertKeyFrame(1.0f, Vector3.Zero, easing);

        visual.StartAnimation("Opacity", opacityAnim);
        visual.StartAnimation("Translation", offsetAnim);
    }

    public void OnGamepadButton(GamepadButton button)
    {
        // PS4/PS5のタッチパッド押し込み: システム設定パネルを開閉する(他のオーバーレイ表示中は何もしない)。
        if (button == GamepadButton.Touchpad)
        {
            if (SystemSettings.IsOpen) SystemSettings.Close();
            else if (!ConsoleEditor.IsOpen && !EditOverlay.IsOpen) SystemSettings.Show();
            return;
        }

        // システム設定パネル表示中は、入力をすべてパネルへ渡す。
        if (SystemSettings.IsOpen)
        {
            SystemSettings.HandleGamepadInput(button);
            return;
        }

        // 編集オーバーレイ表示中は、入力をすべてオーバーレイへ渡す。
        if (ConsoleEditor.IsOpen)
        {
            ConsoleEditor.HandleGamepadInput(button);
            return;
        }

        if (EditOverlay.IsOpen)
        {
            EditOverlay.HandleGamepadInput(button);
            return;
        }

        switch (_focus)
        {
            case FocusArea.Items:
                HandleItemsInput(button);
                break;
            case FocusArea.SystemIcons:
                HandleSystemIconsInput(button);
                break;
        }
    }

    public void OnGamepadButtonLongPress(GamepadButton button) { }

    private void HandleItemsInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadLeft:
                // 小サイズでは、ページをまたぐ移動はL/Rだけ。左端の列から左へは動かない。
                if (_compact && _viewMode == ViewMode.Consoles && (_selectedIndex % CompactPerPage) % CompactColumns == 0)
                    break;
                SetSelectedIndex(_selectedIndex - 1);
                break;
            case GamepadButton.DPadRight:
                if (_compact && _viewMode == ViewMode.Consoles)
                {
                    // 右端の列(またはそのページの最後)から右へ行くと、右のシステムアイコンへ移る。
                    int next = _selectedIndex + 1;
                    if ((_selectedIndex % CompactPerPage) % CompactColumns == CompactColumns - 1
                        || next >= Consoles.Count || next / CompactPerPage != _selectedIndex / CompactPerPage)
                    {
                        EnterSystemIconsFocus();
                        break;
                    }
                }
                SetSelectedIndex(_selectedIndex + 1);
                break;
            case GamepadButton.DPadUp:
                if (_compact && _viewMode == ViewMode.Consoles) MoveCompactRow(-1);
                break;
            case GamepadButton.DPadDown:
                if (_compact && _viewMode == ViewMode.Consoles && MoveCompactRow(+1)) break;
                if (!IconsOnSide) EnterSystemIconsFocus();
                break;
            case GamepadButton.LeftTrigger:
            case GamepadButton.RightTrigger:
                ToggleViewMode();
                break;
            case GamepadButton.LeftShoulder:
                MoveCompactPage(-1);
                break;
            case GamepadButton.RightShoulder:
                MoveCompactPage(+1);
                break;
            case GamepadButton.Y:
                if (_viewMode == ViewMode.Consoles) ToggleCompact();
                break;
            case GamepadButton.A:
                NavigateToSelected();
                break;
            case GamepadButton.X:
                EditSelected();
                break;
            case GamepadButton.Menu:
                StartBiosBootOfSelected();
                break;
        }
    }

    // ---- 小サイズ表示(2段×5個のページ表示) ----

    private int CompactPageCount => Math.Max(1, (Consoles.Count + CompactPerPage - 1) / CompactPerPage);

    /// <summary>△: 大サイズ(横スクロール)と小サイズ(2段のページ表示)を切り替える。設定は次回起動後も保つ。</summary>
    private void ToggleCompact()
    {
        FrameworkElement outgoing = _compact ? ConsoleCompactPanel : ConsoleScrollViewer;
        _compact = !_compact;
        UpdateTabLabels();
        FrameworkElement incoming = _compact ? ConsoleCompactPanel : ConsoleScrollViewer;

        EmulatorLauncher.Emulation.AppPreferences prefs = EmulatorLauncher.Emulation.AppPreferencesStore.Load();
        prefs.ConsoleViewCompact = _compact;
        EmulatorLauncher.Emulation.AppPreferencesStore.Save(prefs);

        _compactPage = -1;

        // 切り替えのアニメーション: 今の表示は薄れて消え、新しい表示は少し小さい状態から拡大しながら現れる。
        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;
        SetSelectedConsoleIndex(_selectedIndex);

        Storyboard fadeOut = AnimateOpacity(outgoing, 1, 0, 120);
        fadeOut.Completed += (_, _) =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            fadeOut.Stop();
            outgoing.Opacity = 1;
        };

        incoming.UpdateLayout();
        Visual panelVisual = ElementCompositionPreview.GetElementVisual(incoming);
        panelVisual.CenterPoint = new Vector3((float)incoming.ActualWidth / 2, (float)incoming.ActualHeight / 2, 0);
        Compositor compositor = panelVisual.Compositor;
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));
        Vector3KeyFrameAnimation grow = compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0f, new Vector3(0.94f, 0.94f, 1f));
        grow.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f), easing);
        grow.Duration = TimeSpan.FromMilliseconds(260);
        panelVisual.StartAnimation("Scale", grow);

        Storyboard fadeIn = AnimateOpacity(incoming, 0, 1, 220);
        fadeIn.Completed += (_, _) =>
        {
            fadeIn.Stop();
            incoming.Opacity = 1;
        };

        // 表示し直した直後はアイコンがまだ作られていないことがあるので、少し後にもう一度合わせる。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_viewMode == ViewMode.Consoles) SetSelectedConsoleIndex(_selectedIndex);
        });
    }

    /// <summary>表示するページを切り替える(そのページのアイコンだけを並べる)。</summary>
    private void ShowCompactPage(int page)
    {
        page = Math.Clamp(page, 0, CompactPageCount - 1);
        if (page != _compactPage)
        {
            int previous = _compactPage;
            _compactPage = page;
            _compactItems = Consoles.Skip(page * CompactPerPage).Take(CompactPerPage).ToList();
            ConsoleCompactRepeater.ItemsSource = _compactItems;
            // 要素の再生成はレイアウト後なので、その後でもう一度、選択中のカードの拡大を合わせる。
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateCompactScales);

            // 以前のページがあるときだけ、新しいページを移動方向からスライドさせて見せる。
            if (previous >= 0)
            {
                int direction = _compactSlide != 0 ? _compactSlide : (page > previous ? 1 : -1);
                AnimateCompactPageSlide(direction);
            }
            UpdatePageDots(page, animate: previous >= 0);
        }
        else
        {
            UpdatePageDots(page, animate: false);
        }
        _compactSlide = 0;
    }

    /// <summary>ページが変わったとき、新しいページのアイコンを横からスライドさせながら現す。</summary>
    private void AnimateCompactPageSlide(int direction)
    {
        FrameworkElement target = ConsoleCompactRepeater;
        ElementCompositionPreview.SetIsTranslationEnabled(target, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(target);
        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));

        ScalarKeyFrameAnimation slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0f, direction * 140f);
        slide.InsertKeyFrame(1f, 0f, easing);
        slide.Duration = TimeSpan.FromMilliseconds(260);
        visual.StartAnimation("Translation.X", slide);

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// ページの点を更新する。現在のページは細長いカプセルで、移るときは進む向きへいったん伸びてから、
    /// 新しい位置へ縮む(Switchのホーム画面のページ表示と同じ動き)。
    /// </summary>
    private void UpdatePageDots(int page, bool animate)
    {
        int pages = CompactPageCount;
        UpdateTabLabels();
        if (pages <= 1)
        {
            ConsolePageDots.Visibility = Visibility.Collapsed;
            return;
        }
        ConsolePageDots.Visibility = Visibility.Visible;

        // ページ数が変わったら、点を作り直す。
        if (_dotPages != pages || _dotPill is null)
        {
            _pillStoryboard?.Stop();
            ConsolePageDots.Children.Clear();
            _dotPages = pages;

            Brush dark = Application.Current.Resources.TryGetValue("HintBadgeBorderBrush", out object? found) && found is Brush b
                ? b
                : new SolidColorBrush(Microsoft.UI.Colors.DimGray);
            for (int i = 0; i < pages; i++)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = DotSize,
                    Height = DotSize,
                    Fill = dark,
                    Opacity = 0.3,
                };
                Canvas.SetLeft(dot, i * DotSpacing + (PillWidth - DotSize) / 2);
                Canvas.SetTop(dot, (DotsHeight - DotSize) / 2);
                ConsolePageDots.Children.Add(dot);
            }

            _dotPill = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = PillWidth,
                Height = DotSize,
                RadiusX = DotSize / 2,
                RadiusY = DotSize / 2,
                Fill = dark,
            };
            Canvas.SetTop(_dotPill, (DotsHeight - DotSize) / 2);
            Canvas.SetLeft(_dotPill, page * DotSpacing);
            ConsolePageDots.Children.Add(_dotPill);
            ConsolePageDots.Width = (pages - 1) * DotSpacing + PillWidth;
            return;
        }

        double targetLeft = page * DotSpacing;
        double curLeft = Canvas.GetLeft(_dotPill);
        double curWidth = _dotPill.Width;

        // 途中のアニメーションがあれば、その時点の位置から続ける。
        _pillStoryboard?.Stop();
        Canvas.SetLeft(_dotPill, curLeft);
        _dotPill.Width = curWidth;

        if (!animate)
        {
            Canvas.SetLeft(_dotPill, targetLeft);
            _dotPill.Width = PillWidth;
            return;
        }

        // 1段目: 進む向きへ伸びて、元の位置と新しい位置の両方を覆う。
        // 2段目: 新しい位置の幅まで縮む。
        double spanLeft = Math.Min(curLeft, targetLeft);
        double spanRight = Math.Max(curLeft + curWidth, targetLeft + PillWidth);
        TimeSpan stretchEnd = TimeSpan.FromMilliseconds(130);
        TimeSpan shrinkEnd = TimeSpan.FromMilliseconds(290);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        DoubleAnimationUsingKeyFrames Track(string property, double from, double mid, double to)
        {
            var anim = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = stretchEnd, Value = mid, EasingFunction = easing });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = shrinkEnd, Value = to, EasingFunction = easing });
            Storyboard.SetTarget(anim, _dotPill);
            Storyboard.SetTargetProperty(anim, property);
            return anim;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(Track("(Canvas.Left)", curLeft, spanLeft, targetLeft));
        storyboard.Children.Add(Track("Width", curWidth, spanRight - spanLeft, PillWidth));
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            if (_dotPill is null) return;
            Canvas.SetLeft(_dotPill, targetLeft);
            _dotPill.Width = PillWidth;
        };
        _pillStoryboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>L/R: 次/前のページへ(最後↔最初でつながる)。同じ位置のアイコンを選ぶ(無ければそのページの最後)。</summary>
    private void MoveCompactPage(int delta)
    {
        if (_viewMode != ViewMode.Consoles || Consoles.Count == 0) return;
        if (!_compact)
        {
            MoveLargePage(delta);
            return;
        }
        int pages = CompactPageCount;
        if (pages <= 1) return;

        int page = _selectedIndex / CompactPerPage;
        int newPage = ((page + delta) % pages + pages) % pages;
        int offset = _selectedIndex % CompactPerPage;
        _compactSlide = delta > 0 ? 1 : -1;
        SetSelectedConsoleIndex(Math.Min(newPage * CompactPerPage + offset, Consoles.Count - 1));
    }

    /// <summary>大サイズ(横スクロール)のとき、L/Rで「今見えている個数ぶん」ずつコンソールを送る。
    /// 端で止まり、すでに端にいるときは反対側の端へ回る。</summary>
    private void MoveLargePage(int delta)
    {
        int last = Consoles.Count - 1;
        int perPage = 5;
        if (ConsoleItemsControl.ContainerFromIndex(_selectedIndex) is FrameworkElement el && el.ActualWidth > 0
            && ConsoleScrollViewer.ViewportWidth > 0)
        {
            perPage = Math.Max(1, (int)(ConsoleScrollViewer.ViewportWidth / el.ActualWidth));
        }

        int target = _selectedIndex + delta * perPage;
        if (delta > 0 && _selectedIndex >= last) target = 0;
        else if (delta < 0 && _selectedIndex <= 0) target = last;
        SetSelectedConsoleIndex(Math.Clamp(target, 0, last));
    }

    /// <summary>上下の段へ移る。移れたらtrue(下の段が無い/最下段ならfalse)。</summary>
    private bool MoveCompactRow(int delta)
    {
        if (Consoles.Count == 0) return false;
        int row = (_selectedIndex % CompactPerPage) / CompactColumns;
        int newRow = row + delta;
        if (newRow < 0 || newRow >= CompactRows) return false;

        int target = _selectedIndex + delta * CompactColumns;
        if (target < 0 || target >= Consoles.Count) return false;
        SetSelectedConsoleIndex(target);
        return true;
    }

    /// <summary>小サイズのアイコンが作られたとき、選択時の拡大アニメーションを仕込む。</summary>
    private void CompactRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element) return;

        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        double width = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        double height = double.IsNaN(element.Height) ? element.ActualHeight : element.Height;
        visual.CenterPoint = new Vector3((float)width / 2, (float)height / 2, 0);

        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));
        Vector3KeyFrameAnimation scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Target = "Scale";
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(180);
        scaleAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
        ImplicitAnimationCollection implicitAnimations = compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Scale"] = scaleAnimation;
        visual.ImplicitAnimations = implicitAnimations;

        // ページ切り替え直後は、要素のDataContextがまだ前のページのものを指すことがあるため、
        // 添字から今のページの項目を引いて、選択中かどうかを決める。
        bool selected = args.Index >= 0 && args.Index < _compactItems.Count
            ? _compactItems[args.Index].IsSelected
            : (element.DataContext as ISelectableCard)?.IsSelected == true;
        UpdateCardScale(visual, selected);
    }

    /// <summary>表示中の小サイズのアイコンの拡大(選択中だけ大きく)を、選択状態に合わせる。</summary>
    private void UpdateCompactScales()
    {
        for (int i = 0; i < _compactItems.Count; i++)
        {
            if (ConsoleCompactRepeater.TryGetElement(i) is FrameworkElement element)
            {
                UpdateCardScale(ElementCompositionPreview.GetElementVisual(element), _compactItems[i].IsSelected);
            }
        }
    }

    private void EditSelected()
    {
        if (_viewMode == ViewMode.Consoles)
        {
            if (Consoles.Count == 0 || _selectedIndex < 0 || _selectedIndex >= Consoles.Count) return;
            ConsoleEditor.Show(Consoles[_selectedIndex]);
            return;
        }

        if (_viewMode != ViewMode.Collections) return;
        if (Collections.Count == 0 || _selectedIndex < 0 || _selectedIndex >= Collections.Count) return;

        RomCollectionInfo selected = Collections[_selectedIndex];
        if (selected.IsAddNewCard) return;

        EditOverlay.Show(selected);
    }

    private void NavigateToSelected()
    {
        if (_viewMode == ViewMode.Consoles)
        {
            if (Consoles.Count == 0 || _selectedIndex < 0 || _selectedIndex >= Consoles.Count) return;
            Frame.Navigate(typeof(GameSelectPage), Consoles[_selectedIndex]);
        }
        else
        {
            if (Collections.Count == 0 || _selectedIndex < 0 || _selectedIndex >= Collections.Count) return;

            RomCollectionInfo selected = Collections[_selectedIndex];
            if (selected.IsAddNewCard)
            {
                // 新規追加の既定名(「コレクション1」「コレクション2」…)を決めるため、
                // 表示中のコレクション名を渡す(「新規追加」カード自体は除く)。
                EditOverlay.Show(null, Collections.Where(c => !c.IsAddNewCard).Select(c => c.Name));
                return;
            }

            // コレクションはStaggeredLayout(詰めて並べる表示)+FocusManager.TryMoveFocusによる
            // 専用ページ(CollectionGameSelectPage)へ遷移する。GameSelectPageは均等格子の
            // GridViewを前提にした選択ロジック(index±列数)のままにしておくリスク低減方針
            // (PROJECT_NOTES.md参照)のため、コレクション表示側だけこちらへ切り出した。
            Frame.Navigate(typeof(CollectionGameSelectPage), selected);
        }
    }

    /// <summary>小サイズのコンソール一覧のときだけ、システムアイコンを右端に縦に並べる(一覧の高さを確保するため)。</summary>
    private bool? _iconsSideApplied;
    private bool IconsOnSide => _compact && _viewMode == ViewMode.Consoles;

    private void ApplySystemIconLayout()
    {
        bool side = IconsOnSide;
        if (_iconsSideApplied == side) return;
        _iconsSideApplied = side;
        Grid.SetRow(SystemIconPanel, side ? 1 : 2);
        SystemIconPanel.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
        SystemIconPanel.HorizontalAlignment = side ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        SystemIconPanel.Spacing = side ? 22 : 36;
        SystemIconPanel.Margin = side ? new Thickness(0, 0, 36, 0) : new Thickness(0, 8, 0, 24);
        foreach (UIElement child in SystemIconPanel.Children)
        {
            if (child is not Button b) continue;
            double size = side ? 96 : 130;
            b.Width = size;
            b.Height = size;
            if (b.Content is Image img) { img.Width = side ? 66 : 90; img.Height = img.Width; }
        }
        UpdateSystemIconHighlight();
    }

    private void EnterSystemIconsFocus()
    {
        _focus = FocusArea.SystemIcons;
        _systemIconIndex = 0;
        UpdateSystemIconHighlight();
        RefreshItemHighlight();

        SelectedLogoImage.Source = null;
        SelectedLogoImage.Opacity = 0;
    }

    private void ExitSystemIconsFocus()
    {
        _focus = FocusArea.Items;
        UpdateSystemIconHighlight();
        RefreshItemHighlight();

        if (_viewMode == ViewMode.Consoles && Consoles.Count > 0)
        {
            UpdateSelectedLogo(Consoles[_selectedIndex].LogoPath);
        }
    }

    private void HandleSystemIconsInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadLeft:
                // 右端に縦並びのときは、左へ押すと一覧へ戻る。
                if (IconsOnSide) ExitSystemIconsFocus();
                else SetSystemIconIndex(_systemIconIndex - 1);
                break;
            case GamepadButton.DPadRight:
                if (!IconsOnSide) SetSystemIconIndex(_systemIconIndex + 1);
                break;
            case GamepadButton.LeftTrigger:
            case GamepadButton.RightTrigger:
                ToggleViewMode();
                break;
            case GamepadButton.DPadUp:
                if (IconsOnSide) SetSystemIconIndex(_systemIconIndex - 1);
                else ExitSystemIconsFocus();
                break;
            case GamepadButton.DPadDown:
                if (IconsOnSide) SetSystemIconIndex(_systemIconIndex + 1);
                break;
            // アイコンにフォーカスがあっても、△(表示切替)はコンソール一覧で使える。
            case GamepadButton.Y:
                if (_viewMode == ViewMode.Consoles) ToggleCompact();
                break;

            // アイコンにフォーカスがあっても、L/Rでページを移動できる。
            case GamepadButton.LeftShoulder:
                if (IconsOnSide) MoveCompactPage(-1);
                break;
            case GamepadButton.RightShoulder:
                if (IconsOnSide) MoveCompactPage(+1);
                break;
            case GamepadButton.B:
                ExitSystemIconsFocus();
                break;
            case GamepadButton.A:
                ActivateSystemIcon(_systemIconIndex);
                break;
        }
    }

    private void SetSystemIconIndex(int index)
    {
        _systemIconIndex = Math.Clamp(index, 0, SystemIconCount - 1);
        UpdateSystemIconHighlight();
    }

    private static readonly EmulatorLauncher.Converters.BoolToBrushConverter PulseBrushConverter = new();

    private void UpdateSystemIconHighlight()
    {
        var buttons = new[] { ControllerIconBtn, SettingsIconBtn, SystemSettingsIconBtn, MaximizeIconBtn, PowerIconBtn };
        for (int i = 0; i < buttons.Length; i++)
        {
            bool active = _focus == FocusArea.SystemIcons && i == _systemIconIndex;
            var button = buttons[i];

            // 選択中は、コンソール/コレクションのカードの選択枠と同じ、水色の脈動ブラシ(全体で共有)。
            button.BorderBrush = active
                ? (Brush)PulseBrushConverter.Convert(true, typeof(Brush), null!, string.Empty)
                : new SolidColorBrush(Microsoft.UI.Colors.LightGray);
            button.BorderThickness = new Thickness(active ? 5 : 3);

            AnimateIconScale(button, active);
        }
    }

    /// <summary>
    /// 【修正箇所】ActualWidth / ActualHeight を使用して NaN を防止
    /// </summary>
    private static void AnimateIconScale(Button button, bool active)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(button);
        double width = double.IsNaN(button.Width) ? button.ActualWidth : button.Width;
        double height = double.IsNaN(button.Height) ? button.ActualHeight : button.Height;

        visual.CenterPoint = new Vector3((float)width / 2, (float)height / 2, 0);

        Compositor compositor = visual.Compositor;
        float scale = active ? 1.12f : 1.0f;

        Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Duration = TimeSpan.FromMilliseconds(140);
        scaleAnim.InsertKeyFrame(1.0f, new Vector3(scale, scale, 1.0f));

        visual.StartAnimation("Scale", scaleAnim);
    }

    /// <summary>システムアイコンバーの左から0始まりのインデックス(操作=0,設定=1,システム設定=2,フルスクリーン=3,終了=4)に
    /// 対応する処理を実行する。</summary>
    private void ActivateSystemIcon(int index)
    {
        switch (index)
        {
            case 0:
                Frame.Navigate(typeof(ControllerSettingsPage));
                break;
            case 1:
                Frame.Navigate(typeof(SettingsPage));
                break;
            case 2:
                SystemSettings.Show();
                break;
            case 3:
                AppServices.MainWindow?.ToggleFullScreen();
                break;
            case 4:
                AppServices.MainWindow?.Close();
                break;
        }
    }

    private void SystemIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        int index = button.Name switch
        {
            "ControllerIconBtn" => 0,
            "SettingsIconBtn" => 1,
            "SystemSettingsIconBtn" => 2,
            "MaximizeIconBtn" => 3,
            "PowerIconBtn" => 4,
            _ => -1,
        };
        if (index < 0) return;

        _focus = FocusArea.SystemIcons;
        SetSystemIconIndex(index);
        ActivateSystemIcon(index);
    }

    private void CollectionIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 【修正箇所】ActualWidth / ActualHeight を使用して NaN を防止
    /// </summary>
    private void CardBorder_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ISelectableCard info)
            return;

        Visual visual = ElementCompositionPreview.GetElementVisual(border);
        Compositor compositor = visual.Compositor;

        double width = double.IsNaN(border.Width) ? border.ActualWidth : border.Width;
        double height = double.IsNaN(border.Height) ? border.ActualHeight : border.Height;

        visual.CenterPoint = new Vector3((float)width / 2, (float)height / 2, 0);

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

        UpdateCardScale(visual, info.IsSelected);

        info.PropertyChanged -= Info_PropertyChanged;
        info.PropertyChanged += Info_PropertyChanged;

        void Info_PropertyChanged(object? s, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ISelectableCard.IsSelected))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateCardScale(visual, info.IsSelected);
                });
            }
        }
    }

    private void UpdateCardScale(Visual visual, bool isSelected)
    {
        float scale = isSelected ? 1.15f : 1.0f;
        visual.Scale = new Vector3(scale, scale, 1.0f);
    }
}