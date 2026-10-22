using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using EmulatorLauncher.Controls;
using EmulatorLauncher.Converters;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.Input;
using EmulatorLauncher.Models;
using EmulatorLauncher.RomLibrary;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace EmulatorLauncher.Views;

/// <summary>ゲームのタイル1枚分(最近遊んだゲーム/コレクションのゲーム。ダークテーマのコンソール選択画面用)。</summary>
public sealed class RecentGameItem : ISelectableCard
{
    public RomFile Game { get; set; } = null!;
    public string Title => Game.DisplayTitle;
    public string IconPath => Game.IconPath;

    /// <summary>タイルの幅。高さは230固定で、横長のパッケージは330まで、縦長のパッケージは画像の縦横比に合わせて狭くする。</summary>
    public double TileWidth => Math.Clamp(230 * (Game.AspectRatio > 0 ? Game.AspectRatio : 1.0), 120, 330);

    public bool NoImage => string.IsNullOrEmpty(Game.IconPath);
    public string Badge { get; set; } = string.Empty;
    public string BadgeColor { get; set; } = "#4B1FA8";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// ダークテーマ(コンソールとコレクションを統合した暗い画面)のコンソール選択画面。標準の<see cref="ConsoleSelectPage"/>の代わりに、
/// アプリ設定の「テーマ」が「ダーク」のときに最初に表示される。ゲーム選択画面以降は標準のものをそのまま使う。
///
/// 左=メニュー(コンソール/コレクション作成/ゲームコントローラー/設定/最大化/終了)。
/// 右=上にヒーロー(選択中の画像と名前)、その下に縦に並ぶ段。段は上から
/// 「コンソール」「最近遊んだゲーム」「各コレクション(名前=段の名前、中身=含まれるゲーム)」で、それぞれ横にスクロールする。
/// </summary>
public sealed partial class ConsoleSelectDarkPage : Page, IControllerPage
{
    private enum Area { Menu, Content }
    private enum SectionKind { Console, Games }

    private sealed class Section
    {
        public string Key = string.Empty;
        public string Title = string.Empty;
        public SectionKind Kind;
        public IList Items = null!;
        public int Index;
        public RomCollectionInfo? Collection;
        public FrameworkElement Root = null!;
        public ScrollViewer Scroll = null!;
        public ItemsRepeater Repeater = null!;
        public double TileW;
        public double TileH;
        public double Spacing;
    }

    private const int MenuCount = 6;
    private const int RecentLimit = 10;

    public List<ConsoleInfo> Consoles { get; } = new();

    private readonly List<Section> _sections = new();
    private readonly Dictionary<string, int> _indexByKey = new();
    private string? _currentKey;
    private int _sectionIndex;
    private Area _area = Area.Content;
    private int _menuIndex;
    private int _loadVersion;
    private bool _consolesLoaded;
    private (Section Section, int Index)? _lastSelected;
    private DispatcherTimer? _clock;

    private readonly TextBlock[] _menuTexts;
    private readonly Border[] _menuBars;

    public ConsoleSelectDarkPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = NavigationCacheMode.Required;

        _menuTexts = new[] { MenuText0, MenuText1, MenuText2, MenuText3, MenuText4, MenuText5 };
        _menuBars = new[] { MenuBar0, MenuBar1, MenuBar2, MenuBar3, MenuBar4, MenuBar5 };

        // 段をまとめる縦のScrollViewerも、ホイールで勝手にスクロールさせない(ホイール=上下の操作に統一。段の見出しや隙間の上でも同じ)。
        SectionsPanel.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        SectionsPanel.PointerWheelChanged += (_, e) => AppServices.MainWindow?.HandleLauncherWheel(e);

        ConsoleEditor.Closed += (_, saved) =>
        {
            if (saved) _ = ReloadAllAsync();
        };

        // 詳細画面でお気に入り/コレクションへの追加・解除やゲーム情報の編集をしたら、詳細を閉じたときに段を読み直す
        // (開いている間に作り直すと、詳細画面の戻り先のカード探しが狂うため、閉じるまで待つ)。
        bool detailDirty = false;
        DetailOverlay.MembershipChanged += (_, _) => detailDirty = true;
        DetailOverlay.GameEdited += (_, _) => detailDirty = true;
        DetailOverlay.DiscSetChanged += (_, _) => detailDirty = true;
        DetailOverlay.Closed += (_, _) =>
        {
            if (!detailDirty) return;
            detailDirty = false;
            _ = ReloadAllAsync();
        };

        // アプリ設定「ゲーム起動方法」「再開」: ゲーム選択画面と同じく、詳細画面経由か直接起動かを切り替える。
        DetailOverlay.LaunchRequested += (_, _) => StartGame();
        DetailOverlay.ResumeRequested += (_, _) =>
        {
            GamePlayPage.ResumeOnNextLaunch = true;
            GamePlayPage.ResumeStateKey = null;
            StartGame();
        };
        DetailOverlay.StateResumeRequested += (_, key) =>
        {
            GamePlayPage.ResumeOnNextLaunch = true;
            GamePlayPage.ResumeStateKey = key;
            StartGame();
        };

        RefreshAll();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _launchViaDetail = AppPreferencesStore.Load().LaunchViaDetail;
        // このページはキャッシュされるため、詳細画面を開いたままゲームを起動すると、終了後に詳細画面が開いた状態で戻ってくる。
        // 直接起動やコンソール側からの起動と同じく、戻ったときは詳細を閉じた状態にする(戻りアニメーションは不要)。
        if (DetailOverlay.IsOpen)
        {
            DetailOverlay.ReturnTargetResolver = null;
            DetailOverlay.Close();
        }
        StartClock();
        _ = ReloadAllAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _clock?.Stop();
    }

    // ---- 時計 ----

    private void StartClock()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        if (_clock is null)
        {
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm");
        }
        _clock.Start();
    }

    // ---- 読み込み ----

    private static string BadgeFor(string consoleId) => consoleId.ToUpperInvariant();

    private static List<RecentGameItem> ToTiles(IEnumerable<RomFile> files, Dictionary<string, ConsoleInfo> byId)
    {
        var list = new List<RecentGameItem>();
        EmulatorLauncher.Services.GameListHelper.ApplyAspectRatios(files);
        foreach (RomFile file in files)
        {
            byId.TryGetValue(file.ConsoleId, out ConsoleInfo? console);
            list.Add(new RecentGameItem
            {
                Game = file,
                Badge = BadgeFor(file.ConsoleId),
                BadgeColor = console?.AccentColorHex ?? "#4B1FA8",
            });
        }
        return list;
    }

    /// <summary>コンソール・最近遊んだゲーム・コレクション(とそのゲーム)を読み直して、段を作り直す。</summary>
    private async Task ReloadAllAsync()
    {
        int version = ++_loadVersion;

        // 作り直しても、選んでいた段と各段の位置はなるべく保つ。
        foreach (Section s in _sections) _indexByKey[s.Key] = s.Index;
        _currentKey = CurrentSection?.Key ?? _currentKey;

        List<ConsoleInfo> allConsoles = SampleData.GetConsoles();
        Dictionary<string, ConsoleInfo> byId = allConsoles.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());

        Dictionary<string, int> counts;
        try { counts = await GameLibraryFacade.GetFileCountsByConsoleIdAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConsoleSelectDarkPage] タイトル数の取得に失敗しました: {ex.Message}");
            counts = new Dictionary<string, int>();
        }

        var consoles = new List<ConsoleInfo>();
        foreach (ConsoleInfo console in allConsoles)
        {
            console.GameCount = counts.TryGetValue(console.Id, out int matched) ? matched : 0;
            if (console.GameCount > 0) consoles.Add(console);
        }
        if (consoles.Count == 0)
            EmulatorLauncher.Services.AppLog.Info("Console", "タイトルのあるコンソールがありません(ROMフォルダが未登録の可能性があります)");

        List<RecentGameItem> recents;
        try
        {
            IReadOnlyList<RomFile> files = await GameLibraryFacade.GetRecentlyPlayedAsync(RecentLimit);
            await GameLibraryFacade.ResolveIconPathsAsync(files);
            recents = ToTiles(files, byId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConsoleSelectDarkPage] 最近遊んだゲームの読み込みに失敗しました: {ex.Message}");
            recents = new List<RecentGameItem>();
        }

        var collectionSections = new List<Section>();
        try
        {
            List<RomCollectionInfo> collections = await CollectionLibraryFacade.GetAllAsync();
            foreach (RomCollectionInfo collection in collections)
            {
                if (collection.GameIds.Count == 0) continue;
                IReadOnlyList<RomFile> files = await GameLibraryFacade.GetFilesByNosAsync(collection.GameIds);
                if (files.Count == 0) continue;
                await GameLibraryFacade.ResolveIconPathsAsync(files);
                collectionSections.Add(new Section
                {
                    Key = "col:" + collection.Id,
                    Title = collection.Name,
                    Kind = SectionKind.Games,
                    Items = ToTiles(files, byId),
                    Collection = collection,
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConsoleSelectDarkPage] コレクションの読み込みに失敗しました: {ex.Message}");
        }

        if (version != _loadVersion) return; // もっと新しい読み込みが始まっている

        Consoles.Clear();
        Consoles.AddRange(consoles);
        _consolesLoaded = true;

        _sections.Clear();
        if (consoles.Count > 0)
            _sections.Add(new Section { Key = "console", Title = "コンソール", Kind = SectionKind.Console, Items = Consoles });
        if (recents.Count > 0)
            _sections.Add(new Section { Key = "recent", Title = "最近遊んだゲーム", Kind = SectionKind.Games, Items = recents });
        _sections.AddRange(collectionSections);

        foreach (Section s in _sections)
        {
            s.Index = _indexByKey.TryGetValue(s.Key, out int saved) ? Math.Clamp(saved, 0, Math.Max(0, s.Items.Count - 1)) : 0;
        }
        int keep = _currentKey is null ? -1 : _sections.FindIndex(s => s.Key == _currentKey);
        _sectionIndex = keep >= 0 ? keep : 0;
        if (_sections.Count == 0) _area = Area.Menu;

        RebuildSections();
        EmptyGuideText.Visibility = consoles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _lastSelected = null;
        RefreshAll();

        // 作り直した直後は横スクロールが先頭に戻っているので、各段を選択位置へ合わせる。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            foreach (Section s in _sections)
            {
                if (s.Index > 0) s.Scroll.ChangeView(Math.Max(0, OffsetOf(s, s.Index) - s.TileW), null, null, true);
            }
            if (_area == Area.Content) BringSectionIntoView();
        });
    }

    /// <summary>段の先頭から、index番目のタイルの左端までの距離(タイルの幅は画像の縦横比で変わる)。</summary>
    private static double OffsetOf(Section s, int index)
    {
        double x = 0;
        for (int i = 0; i < index && i < s.Items.Count; i++)
            x += (s.Items[i] is RecentGameItem g ? g.TileWidth : s.TileW) + s.Spacing;
        return x;
    }

    private Section? CurrentSection => _sectionIndex >= 0 && _sectionIndex < _sections.Count ? _sections[_sectionIndex] : null;

    private void RebuildSections()
    {
        SectionsPanel.Children.Clear();
        foreach (Section s in _sections)
        {
            bool isConsole = s.Kind == SectionKind.Console;
            s.TileW = isConsole ? 240 : 330;
            s.TileH = isConsole ? 240 : 230;
            s.Spacing = isConsole ? 22 : 30;

            var label = new TextBlock
            {
                Text = s.Title,
                FontSize = 32,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE4, 0xE8, 0xF0)),
                Margin = new Thickness(40, 22, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var repeater = new ItemsRepeater
            {
                ItemsSource = s.Items,
                Layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = s.Spacing },
                ItemTemplate = (DataTemplate)Resources[isConsole ? "ConsoleTileTemplate" : "GameTileTemplate"],
            };
            Section captured = s;
            repeater.ElementPrepared += (_, args) =>
            {
                if (args.Element is FrameworkElement el) StyleTile(captured, args.Index, el);
            };

            // 横スクロールの段は、ScrollViewerが縦のホイールを横スクロールに使ってしまう。ホイールは上下の操作(Shift+ホイールで左右)に
            // 統一するため、ScrollViewerの中身(余白と段の隙間を含めて当たり判定を持つ枠)でホイールを先に受けてMainWindowの変換へ回す。
            var rowHost = new Border
            {
                Padding = new Thickness(40, 0, 40, 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = repeater,
            };
            rowHost.PointerWheelChanged += (_, e) => AppServices.MainWindow?.HandleLauncherWheel(e);

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = rowHost,
            };

            // 段の左右の端に、マウスが乗っているときだけ出るページ移動の矢印を重ねる。
            var rowGrid = new Grid();
            rowGrid.Children.Add(scroll);
            rowGrid.Children.Add(new ScrollArrowOverlay(scroll, direction => ScrollArrowPage(captured, direction)));

            var root = new StackPanel { Orientation = Orientation.Vertical };
            root.Children.Add(label);
            root.Children.Add(rowGrid);
            SectionsPanel.Children.Add(root);

            s.Root = root;
            s.Scroll = scroll;
            s.Repeater = repeater;
        }
    }

    // ---- 選択の表示 ----

    private static void SetSelected(object? item, bool selected)
    {
        if (item is ConsoleInfo c) c.IsSelected = selected;
        else if (item is RecentGameItem g) g.IsSelected = selected;
    }

    private bool IsCurrent(Section s, int index)
        => _area == Area.Content && ReferenceEquals(CurrentSection, s) && s.Index == index;

    /// <summary>タイルの拡大(選択中は標準のコンソール選択画面と同じ1.15倍)。</summary>
    private void StyleTile(Section s, int index, FrameworkElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual.ImplicitAnimations is null)
        {
            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));
            Vector3KeyFrameAnimation scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.Duration = TimeSpan.FromMilliseconds(180);
            scaleAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
            ImplicitAnimationCollection implicitAnimations = compositor.CreateImplicitAnimationCollection();
            implicitAnimations["Scale"] = scaleAnimation;
            visual.ImplicitAnimations = implicitAnimations;
        }
        double tileW = double.IsNaN(element.Width) || element.Width <= 0 ? s.TileW : element.Width;
        visual.CenterPoint = new Vector3((float)tileW / 2, (float)s.TileH / 2, 0);
        float scale = IsCurrent(s, index) ? 1.15f : 1.0f;
        visual.Scale = new Vector3(scale, scale, 1f);
    }

    private void ApplySelection()
    {
        (Section, int)? current = null;
        if (_area == Area.Content && CurrentSection is { } cs && cs.Items.Count > 0)
            current = (cs, Math.Clamp(cs.Index, 0, cs.Items.Count - 1));

        if (_lastSelected is { } last && (current is null || !ReferenceEquals(last.Section, current.Value.Item1) || last.Index != current.Value.Item2))
        {
            if (last.Index < last.Section.Items.Count) SetSelected(last.Section.Items[last.Index], false);
            if (last.Section.Repeater?.TryGetElement(last.Index) is FrameworkElement oldEl) StyleTile(last.Section, last.Index, oldEl);
        }

        if (current is { } c)
        {
            SetSelected(c.Item1.Items[c.Item2], true);
            if (c.Item1.Repeater?.TryGetElement(c.Item2) is FrameworkElement el) StyleTile(c.Item1, c.Item2, el);
            _lastSelected = (c.Item1, c.Item2);
        }
        else
        {
            _lastSelected = null;
        }
    }

    /// <param name="slide">ヒーローを動かす向き(+1=右から、-1=左から。0=動かさない)。</param>
    /// <param name="slideY">上下に動かす向き(+1=下から、-1=上から。0=動かさない)。</param>
    private void RefreshAll(int slide = 0, int slideY = 0)
    {
        ApplySelection();
        UpdateMenuVisuals();
        UpdateHero(slide, slideY);

        Section? s = CurrentSection;
        // X: コンソール=編集。ゲーム=「ゲーム起動」(詳細を挟まない)設定のときだけ「詳細」(ゲーム選択画面の□と同じ)。
        string? editLabel = s is null ? null
            : s.Kind == SectionKind.Console ? "コンソール編集"
            : !_launchViaDetail ? "詳細"
            : null;
        // Menu: 選んでいるコンソールが本体(BIOS)起動に対応していれば案内を出す。
        bool biosWanted = s is { Kind: SectionKind.Console } && s.Index >= 0 && s.Index < s.Items.Count
            && s.Items[s.Index] is ConsoleInfo biosConsole && BiosBoot.IsAvailable(biosConsole);
        bool biosShown = ConsoleHintBar.Hints.Contains(_biosHint);
        if (biosWanted && !biosShown) ConsoleHintBar.Hints.Insert(0, _biosHint);
        else if (!biosWanted && biosShown) ConsoleHintBar.Hints.Remove(_biosHint);

        bool shown = ConsoleHintBar.Hints.Contains(EditHint);
        if (editLabel is not null)
        {
            EditHint.Label = editLabel;
            if (!shown) ConsoleHintBar.Hints.Insert(Math.Min(1, ConsoleHintBar.Hints.Count), EditHint);
        }
        else if (shown)
        {
            ConsoleHintBar.Hints.Remove(EditHint);
        }
    }

    private readonly EmulatorLauncher.Controls.HintDefinition _biosHint = new() { Key = BiosBoot.HintKey, Label = BiosBoot.HintLabel };

    private void StartBiosBootOfSelected()
    {
        Section? s = CurrentSection;
        if (s is { Kind: SectionKind.Console } && s.Index >= 0 && s.Index < s.Items.Count && s.Items[s.Index] is ConsoleInfo console)
            BiosBoot.Start(Frame, console);
    }

    /// <summary>アプリ設定「ゲーム起動方法」(true=詳細画面から起動)。画面を開くたびに読み直す。</summary>
    private bool _launchViaDetail = AppPreferencesStore.Load().LaunchViaDetail;

    private void UpdateMenuVisuals()
    {
        for (int i = 0; i < MenuCount; i++)
        {
            bool focused = _area == Area.Menu && i == _menuIndex;
            bool primary = i == 0;
            _menuTexts[i].Foreground = new SolidColorBrush(focused
                ? Microsoft.UI.Colors.White
                : primary ? Windows.UI.Color.FromArgb(255, 0xD5, 0xDA, 0xE5)
                          : Windows.UI.Color.FromArgb(255, 0x6B, 0x72, 0x80));
            _menuTexts[i].FontWeight = focused ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            _menuBars[i].Opacity = focused ? 1 : 0;
        }
    }

    private string _heroImagePath = string.Empty;

    private void UpdateHero(int slide = 0, int slideY = 0)
    {
        string iconPath = string.Empty;
        string badge = string.Empty;
        string badgeColor = "#4B1FA8";
        string name = string.Empty;
        string sub = string.Empty;

        Section? s = CurrentSection;
        object? item = s is not null && s.Index >= 0 && s.Index < s.Items.Count ? s.Items[s.Index] : null;
        if (item is ConsoleInfo c)
        {
            iconPath = string.IsNullOrEmpty(c.HeroPath) ? c.IconPath : c.HeroPath;
            badge = BadgeFor(c.Id);
            badgeColor = c.AccentColorHex;
            name = c.Name;
            sub = $"{c.GameCount} タイトル";
        }
        else if (item is RecentGameItem g)
        {
            ConsoleInfo? console = SampleData.GetConsoles().FirstOrDefault(x => x.Id == g.Game.ConsoleId);
            iconPath = console is null ? string.Empty : string.IsNullOrEmpty(console.HeroPath) ? console.IconPath : console.HeroPath;
            badge = g.Badge;
            badgeColor = g.BadgeColor;
            name = g.Title;
            sub = console?.Name ?? string.Empty;
        }

        // 画像が同じときは作り直さない(メニュー移動などで何度も呼ばれるため、毎回読み直すとヒーローが点滅する)。
        ImageSource? previous = HeroImage.Source;
        if (!string.Equals(iconPath, _heroImagePath, StringComparison.Ordinal))
        {
            HeroImage.Source = string.IsNullOrEmpty(iconPath)
                ? null
                : (ImageSource?)new IconPathToImageSourceConverter().Convert(iconPath, typeof(object), "2800", string.Empty);
            _heroImagePath = iconPath;
        }
        HeroBadge.Visibility = string.IsNullOrEmpty(badge) ? Visibility.Collapsed : Visibility.Visible;
        HeroBadgeText.Text = badge;
        HeroBadge.Background = (Brush)new HexToBrushConverter().Convert(badgeColor, typeof(Brush), null!, string.Empty);
        HeroNameText.Text = name;
        HeroSubText.Text = sub;

        if (slide != 0 || slideY != 0) AnimateHeroSlide(slide, slideY, previous);
    }

    private void HeroGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 横へ動かす画像が、ヒーローの枠からはみ出さないよう切り取る。
        HeroGrid.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private int _heroAnimToken;

    /// <summary>
    /// 選択が左右に動いたとき、ヒーローの画像と名前をその向きに合わせて滑らせる
    /// (新しい画像は動かした側から入り、前の画像は反対側へ出ていく)。
    /// </summary>
    private void AnimateHeroSlide(int direction, int directionY, ImageSource? previous)
    {
        try
        {
            float width = (float)(HeroGrid.ActualWidth > 0 ? HeroGrid.ActualWidth : 1400);
            float height = (float)(HeroGrid.ActualHeight > 0 ? HeroGrid.ActualHeight : 410);
            int token = ++_heroAnimToken;

            HeroImageOld.Source = previous;
            HeroImageOld.Visibility = previous is null ? Visibility.Collapsed : Visibility.Visible;

            Compositor compositor = ElementCompositionPreview.GetElementVisual(HeroImage).Compositor;
            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));
            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            void Slide(UIElement element, Vector3 from, Vector3 to, float fadeFrom, float fadeTo, int ms)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                Visual visual = ElementCompositionPreview.GetElementVisual(element);

                Vector3KeyFrameAnimation move = compositor.CreateVector3KeyFrameAnimation();
                move.InsertKeyFrame(0f, from);
                move.InsertKeyFrame(1f, to, ease);
                move.Duration = TimeSpan.FromMilliseconds(ms);
                visual.StartAnimation("Translation", move);

                ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, fadeFrom);
                fade.InsertKeyFrame(1f, fadeTo, ease);
                fade.Duration = TimeSpan.FromMilliseconds(ms);
                visual.StartAnimation("Opacity", fade);
            }

            Slide(HeroImage, new Vector3(direction * width, directionY * height, 0), Vector3.Zero, 1f, 1f, 320);
            if (previous is not null)
                Slide(HeroImageOld, Vector3.Zero, new Vector3(-direction * width, -directionY * height, 0), 1f, 1f, 320);
            Slide(HeroCaption, new Vector3(direction * 240f, directionY * 90f, 0), Vector3.Zero, 0f, 1f, 320);

            batch.End();
            batch.Completed += (_, _) =>
            {
                if (token == _heroAnimToken) HeroImageOld.Visibility = Visibility.Collapsed;
            };
        }
        catch { /* 演出に失敗しても表示自体は成立している */ }
    }

    // ---- スクロール ----

    /// <summary>選択中の段を、縦スクロールで画面内へ(段の名前ごと)。</summary>
    private void BringSectionIntoView()
    {
        // 右下の操作ガイドの帯に隠れないよう、下側に余裕を含めた範囲を画面内に入れる。
        if (CurrentSection is { } s)
        {
            s.Root.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                TargetRect = new Windows.Foundation.Rect(0, 0, s.Root.ActualWidth, s.Root.ActualHeight + 110),
            });
        }
    }

    /// <summary>選択中のタイルを、その段の横スクロールで画面内へ。</summary>
    private void BringTileIntoView()
    {
        Section? s = CurrentSection;
        if (s is null || s.Items.Count == 0) return;
        if (s.Repeater.GetOrCreateElement(Math.Clamp(s.Index, 0, s.Items.Count - 1)) is FrameworkElement el)
        {
            // 拡大した分と左右の余白も画面内に入るよう、タイルより広い範囲を指定する(左端で切れないように)。
            el.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                TargetRect = new Windows.Foundation.Rect(-48, 0, (double.IsNaN(el.Width) || el.Width <= 0 ? s.TileW : el.Width) + 96, s.TileH + 110),
            });
        }
    }

    // ---- ゲームパッド入力 ----

    public void OnGamepadButton(GamepadButton button)
    {
        // PS4/PS5のタッチパッド押し込み: システム設定パネルを開閉する(他のオーバーレイ表示中は何もしない)。
        if (button == GamepadButton.Touchpad)
        {
            if (SystemSettings.IsOpen) SystemSettings.Close();
            else if (!ConsoleEditor.IsOpen && !DetailOverlay.IsOpen) SystemSettings.Show();
            return;
        }

        if (SystemSettings.IsOpen) { SystemSettings.HandleGamepadInput(button); return; }
        if (ConsoleEditor.IsOpen) { ConsoleEditor.HandleGamepadInput(button); return; }
        if (DetailOverlay.IsOpen) { DetailOverlay.HandleGamepadInput(button); return; }

        if (_area == Area.Menu) HandleMenuInput(button);
        else HandleContentInput(button);
    }

    public void OnGamepadButtonLongPress(GamepadButton button) { }

    private void FocusMenu()
    {
        _area = Area.Menu;
        _menuIndex = 0;
        RefreshAll();
    }

    private void EnterContent()
    {
        if (_sections.Count == 0) return;
        _area = Area.Content;
        _menuIndex = 0;   // 左で戻った時は、元いた項目(最大化など)ではなく「コンソール」を選んだ状態にする
        RefreshAll();
        BringSectionIntoView();
    }

    private void HandleMenuInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                MoveMenu(-1);
                break;
            case GamepadButton.DPadDown:
                MoveMenu(1);
                break;
            case GamepadButton.DPadRight:
                EnterContent();   // どのメニュー項目からでも、右でコンソール(コンテンツ)へ移る
                break;
            case GamepadButton.A:
                ActivateMenu(_menuIndex);
                break;
        }
    }

    /// <summary>左メニューを上下に動かす。端でこれ以上動かせない時は揺らし、動いた時は選択バーを滑らせる。</summary>
    private void MoveMenu(int delta)
    {
        int old = _menuIndex;
        int target = Math.Clamp(old + delta, 0, MenuCount - 1);
        if (target == old)
        {
            ShakeAnimation.Shake(_menuTexts[old], vertical: true);
            return;
        }

        _menuIndex = target;
        RefreshAll();
        HighlightSlide.SlideElement(_menuBars[_menuIndex], _menuBars[old]);
    }

    /// <summary>段の左右の矢印を押したとき: その段へフォーカスを移し、見えている枚数ぶん選択を左右へ動かす。</summary>
    private void ScrollArrowPage(Section s, int direction)
    {
        if (SystemSettings.IsOpen || ConsoleEditor.IsOpen || DetailOverlay.IsOpen) return;
        int index = _sections.IndexOf(s);
        if (index < 0) return;

        bool changed = _area != Area.Content || _sectionIndex != index;
        if (_area != Area.Content) { _area = Area.Content; _menuIndex = 0; }
        _sectionIndex = index;
        if (changed)
        {
            RefreshAll();
            BringSectionIntoView();
        }
        PageTiles(s, direction);
    }

    /// <summary>L/Rでページ送り。画面に見えている枚数ぶん、選択を左右へ動かす(端では端まで)。</summary>
    private void PageTiles(Section s, int direction)
    {
        if (s.Items.Count == 0) return;
        int page = Math.Max(1, (int)(s.Scroll.ViewportWidth / (s.TileW + s.Spacing)));
        int target = Math.Clamp(s.Index + direction * page, 0, s.Items.Count - 1);
        if (target == s.Index)
        {
            ShakeSelectedTile(vertical: false);
            return;
        }

        s.Index = target;
        RefreshAll(direction);
        BringTileIntoView();
    }

    /// <summary>選択中のタイルを揺らす(これ以上その方向へ動かせない時)。</summary>
    private void ShakeSelectedTile(bool vertical)
    {
        Section? s = CurrentSection;
        if (s is null) return;
        ShakeAnimation.Shake(s.Repeater.TryGetElement(Math.Clamp(s.Index, 0, Math.Max(0, s.Items.Count - 1))) as UIElement, vertical);
    }

    private void HandleContentInput(GamepadButton button)
    {
        Section? s = CurrentSection;
        if (s is null) { FocusMenu(); return; }

        switch (button)
        {
            case GamepadButton.DPadLeft:
                if (s.Index == 0) FocusMenu();
                else { s.Index--; RefreshAll(-1); BringTileIntoView(); }
                break;
            case GamepadButton.DPadRight:
                if (s.Index < s.Items.Count - 1) { s.Index++; RefreshAll(1); BringTileIntoView(); }
                else ShakeSelectedTile(vertical: false);
                break;
            case GamepadButton.DPadUp:
                if (_sectionIndex > 0) { _sectionIndex--; RefreshAll(0, -1); BringSectionIntoView(); }
                else ShakeSelectedTile(vertical: true);
                break;
            case GamepadButton.DPadDown:
                if (_sectionIndex < _sections.Count - 1) { _sectionIndex++; RefreshAll(0, 1); BringSectionIntoView(); }
                else ShakeSelectedTile(vertical: true);
                break;
            case GamepadButton.LeftShoulder:
                PageTiles(s, -1);
                break;
            case GamepadButton.RightShoulder:
                PageTiles(s, 1);
                break;
            case GamepadButton.B:
                FocusMenu();
                break;
            case GamepadButton.A:
                ActivateSelected();
                break;
            case GamepadButton.X:
                EditSelected();
                break;
            case GamepadButton.Menu:
                StartBiosBootOfSelected();
                break;
        }
    }

    // ---- 決定・編集・起動 ----

    private void ActivateSelected()
    {
        Section? s = CurrentSection;
        if (s is null || s.Index < 0 || s.Index >= s.Items.Count) return;

        object item = s.Items[s.Index];
        if (item is ConsoleInfo console)
        {
            Frame.Navigate(typeof(GameSelectPage), console);
        }
        else if (item is RecentGameItem tile)
        {
            if (_launchViaDetail) ShowDetail(tile);
            else
                _ = LaunchGameAsync(tile.Game);
        }
    }

    /// <summary>詳細画面で選ばれた内容(起動/再開/ステート)で、表示中のゲームを起動する。</summary>
    private void StartGame()
    {
        if (DetailOverlay.CurrentGame is not { } game) return;

        // 詳細のカバー画像が、ゲーム画面へ拡大していく連結アニメーション。
        try
        {
            Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView()
                .PrepareToAnimate(GamePlayPage.LaunchCoverKey, DetailOverlay.CoverElement);
        }
        catch { }
        Frame.Navigate(typeof(GamePlayPage), game);
    }

    /// <summary>そのゲームのタイル内のパッケージ画像。画面外で未生成(仮想化)ならnull。</summary>
    private UIElement? FindCoverImage(RomFile game)
    {
        // 選択中のセクションを優先し、無ければ全セクションから探す。
        IEnumerable<Section> order = CurrentSection is { } cur ? new[] { cur }.Concat(_sections) : _sections;
        foreach (Section s in order)
        {
            for (int i = 0; i < s.Items.Count; i++)
            {
                if (s.Items[i] is RecentGameItem item && (ReferenceEquals(item.Game, game) || item.Game.No == game.No))
                {
                    if (s.Repeater?.TryGetElement(i) is FrameworkElement el
                        && GameDetailOverlay.FindFirstImage(el) is { Visibility: Visibility.Visible, ActualWidth: > 0 } image)
                        return image;
                    break;
                }
            }
        }
        return null;
    }

    private void EditSelected()
    {
        Section? s = CurrentSection;
        if (s is null || s.Index < 0 || s.Index >= s.Items.Count) return;

        if (s.Items[s.Index] is ConsoleInfo console) ConsoleEditor.Show(console);
        else if (s.Items[s.Index] is RecentGameItem tile && !_launchViaDetail) ShowDetail(tile);   // 「ゲーム起動」設定のときだけ、Xで詳細を見られる
    }

    private void ShowDetail(RecentGameItem tile)
    {
        // ゲーム選択画面と同じく、タイルのパッケージ画像が詳細のカバーへ移動して開く(閉じる時は元のタイルへ戻る)。
        DetailOverlay.ReturnTargetResolver = FindCoverImage;
        DetailOverlay.Show(tile.Game, FindCoverImage(tile.Game), showLaunch: _launchViaDetail);
    }

    /// <summary>ゲームを、詳細画面を挟まずにそのまま起動する。</summary>
    private async Task LaunchGameAsync(RomFile game)
    {
        if (AppPreferencesStore.Load().ResumeOnDirectLaunch && await GameDetailOverlay.HasSuspendStateAsync(game))
            GamePlayPage.ResumeOnNextLaunch = true;
        Frame.Navigate(typeof(GamePlayPage), game);
    }

    // ---- メニュー(コンソール/コレクション作成/ゲームコントローラー/設定/最大化/終了) ----

    private void ActivateMenu(int menuIndex)
    {
        switch (menuIndex)
        {
            case 0: EnterContent(); break;
            case 1: SystemSettings.Show(); break;
            case 2: Frame.Navigate(typeof(SettingsPage)); break;
            case 3: Frame.Navigate(typeof(ControllerSettingsPage)); break;
            case 4: AppServices.MainWindow?.ToggleFullScreen(); break;
            case 5: AppServices.MainWindow?.Close(); break;
        }
    }

    private void MenuRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Name: { Length: > 0 } name }) return;
        if (!int.TryParse(name.Replace("MenuRow", string.Empty), out int index)) return;

        // 左上の「コンソール」(見出し)は、右クリック(B)と同じく左側のメニューへフォーカスを戻すだけ。
        if (index == 0)
        {
            FocusMenu();
            return;
        }

        _area = Area.Menu;
        _menuIndex = index;
        RefreshAll();
        ActivateMenu(index);
    }

    /// <summary>
    /// タイル(コンソール/最近遊んだゲーム等)をクリック/タップしたとき。フォーカスされていないタイルは、まずフォーカスを移すだけ。
    /// フォーカス中のタイルをもう一度押すと決定(Aと同じ)。
    /// </summary>
    private void Tile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (SystemSettings.IsOpen || ConsoleEditor.IsOpen || DetailOverlay.IsOpen) return;
        if (sender is not FrameworkElement tile) return;

        // ItemsRepeaterの要素にはDataContextが入らないので、どの行の何番目かはリピーターに聞く。
        for (int si = 0; si < _sections.Count; si++)
        {
            Section s = _sections[si];
            int index = s.Repeater.GetElementIndex(tile);
            if (index < 0) continue;

            if (_area == Area.Content && _sectionIndex == si && s.Index == index)
            {
                ActivateSelected();
                return;
            }

            _area = Area.Content;
            _menuIndex = 0;
            _sectionIndex = si;
            s.Index = index;
            RefreshAll();
            BringSectionIntoView();
            BringTileIntoView();
            return;
        }
    }

    private void Image_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image) image.Visibility = Visibility.Collapsed;
    }
}
