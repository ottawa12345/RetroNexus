using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using EmulatorLauncher.Converters;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmulatorLauncher.Controls;

/// <summary>
/// ゲーム詳細オーバーレイ(タイトル/発売元/説明+「コレクションに追加」チップ)の共通UserControl。
///
/// 元々GameSelectPageの中にあった ShowDetail / LoadDetailCollectionsAsync / チップ操作 /
/// CloseDetail をそのまま移したもの。このコントロールが知っているのは「今どのRomFileの詳細を
/// 出しているか(<see cref="CurrentGame"/>)」だけで、ページ側のグリッドの実装
/// (GridViewのインデックス選択か、StaggeredPanelのXAMLフォーカス選択か)には依存しない。
///
/// ページ側の使い方:
/// - Xボタン等で <see cref="Show"/> を呼ぶ。
/// - 表示中のゲームパッド入力は <see cref="HandleGamepadInput"/> へそのまま渡す
///   (Bで自分自身を閉じ、<see cref="Closed"/> を発火する)。
/// - コレクションへの所属が変わったことを知りたい場合は <see cref="MembershipChanged"/> を購読する
///   (CollectionGameSelectPageが「表示中のコレクションから外されたゲーム」を一覧から除くために使う)。
/// </summary>
public sealed partial class GameDetailOverlay : UserControl
{
    private static readonly HexToBrushConverter HexConverter = new();
    private static readonly BoolToBrushConverter BrushConverter = new();
    private static readonly IconPathToImageSourceConverter IconConverter = new();

    /// <summary>「コレクションに追加」欄に表示する、コレクションごとのトグル項目。
    /// Show()のたびに、表示するゲームの所属状況で作り直す。</summary>
    public ObservableCollection<CollectionChipItem> DetailCollections { get; } = new();

    /// <summary>全コレクション(表示の有無によらず)。「ほかのコレクション」画面の一覧と、所属の更新に使う。</summary>
    private readonly List<CollectionChipItem> _allCollectionItems = new();

    /// <summary>チップに並べるコレクションの最大数。これを超えるときは、末尾に「ほかのコレクション…」を足す
    /// (チップは3列×2行=6マスなので、5個+「ほかのコレクション…」)。ちょうど6個以下なら全部並べる。</summary>
    private const int MaxCollectionChips = 5;
    private const string MoreChipId = "*more*";

    /// <summary>説明文の下に並べる、ステートセーブの画像(最大<see cref="MaxStateImages"/>枚)。Show()のたびに作り直す。</summary>
    public ObservableCollection<StateImageItem> StateImages { get; } = new();
    private const int MaxStateImages = 4;

    /// <summary>現在オーバーレイを表示中かどうか。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>現在詳細を表示しているゲーム(閉じている間はnull)。</summary>
    public RomFile? CurrentGame { get; private set; }

    /// <summary>現在ゲームパッドがフォーカスしているチップのDetailCollections内インデックス
    /// (未フォーカス/リスト空なら-1)。</summary>
    private int _chipIndex = -1;

    /// <summary>ゲームパッドのフォーカスが「ランチャー変更」欄にあるか(falseならチップ側)。</summary>

    /// <summary>「起動」ボタンを表示しているか(アプリ設定「ゲーム起動方法」=詳細画面から起動の時)。</summary>
    private bool _launchVisible;

    /// <summary>ゲームパッドのフォーカスが「起動」ボタンにあるか。</summary>
    private bool _launchFocused;


    /// <summary>「再開」ボタンを表示しているか(前回「タイトル選択にもどる」で保存した再開用の状態があるとき)。</summary>
    private bool _resumeVisible;

    /// <summary>ゲームパッドのフォーカスが「再開」ボタンにあるか。</summary>
    private bool _resumeFocused;

    /// <summary>ゲームパッドのフォーカスがステート画像の列にあるか、と、その列の中の位置。</summary>
    private bool _statesFocused;
    private int _stateIndex;

    private bool TopFocused => _launchFocused || _resumeFocused || _statesFocused || _variantFocused;

    // ---- バージョン選択(アーケードのクローン等) ----

    /// <summary>
    /// 代表ゲーム(一覧に出ている1本)に対し、選べるバージョン(代表を含む全部)を返す関数。
    /// 2本以上返ると、「詳細画面から起動」の時だけバージョン選択欄を出す。使う側のページが設定する。
    /// </summary>
    public Func<RomFile, IReadOnlyList<RomFile>>? VariantsResolver { get; set; }

    private IReadOnlyList<RomFile> _variants = Array.Empty<RomFile>();
    private bool _variantVisible;
    private bool _variantFocused;
    private bool _switchingVariant;

    // ---- ディスク選択(複数枚組=.m3uを、何枚目から始めるか。バージョン欄を流用する) ----
    private bool _discMode;
    private List<string> _discNames = new();
    private int _discIndex;
    private bool _discIsFloppy;

    /// <summary>ステート画像が選ばれた(A/クリック)。引数=再開するステート("suspend"/"auto"/スロット番号)。
    /// 使う側のページが、そのステートから続きでゲームを起動する。</summary>
    public event EventHandler<string>? StateResumeRequested;

    /// <summary>「再開」が選ばれた(A/クリック)。使う側のページが、再開用の状態から続きでゲームを起動する。</summary>
    public event EventHandler? ResumeRequested;

    /// <summary>「起動」が選ばれた(A/クリック)。使う側のページがゲームを起動する。</summary>
    public event EventHandler? LaunchRequested;

    /// <summary>詳細のカバー画像(起動時の連結アニメーションの出発点に使う)。</summary>
    public UIElement CoverElement => DetailThumbnailImage;

    /// <summary>選べるランチャー。先頭(null)は「既定(コンソールの既定ランチャー)」で、保存値は空文字。</summary>
    private readonly List<LauncherInfo?> _launcherOptions = new();
    private int _launcherIndex;
    private string _defaultLauncherName = string.Empty;

    /// <summary>DB更新中のチップ。処理が終わる前に同じチップでA(またはクリック)が
    /// 繰り返された場合に、二重に追加/解除しないよう無視するために使う。</summary>
    private readonly HashSet<CollectionChipItem> _applyingChips = new();

    /// <summary>オーバーレイが閉じられた(Bボタン、またはページ側からのClose())ときに発火する。</summary>
    public event EventHandler? Closed;

    /// <summary>「ゲーム情報の編集」で保存したときに発火する(一覧側の表示を更新するため)。</summary>
    public event EventHandler<RomFile>? GameEdited;

    /// <summary>複数枚(ディスク)のまとめ/解除を行ったときに発火する(一覧のゲームが増減するため、一覧側が読み直す)。
    /// 続けて、この詳細画面は閉じる(Closed)。</summary>
    public event EventHandler? DiscSetChanged;

    /// <summary>DiscSetChangedの直前にセットされる。まとめた直後に一覧でフォーカスしたいゲームのNo(解除のときは元の1枚目のディスク)。</summary>
    public int? DiscSetFocusNo { get; private set; }

    /// <summary>チップ操作でコレクションへの所属がDB上で実際に変わったときに発火する。
    /// DB更新は非同期のため、オーバーレイを閉じた後で発火することもある点に注意
    /// (A→Bを素早く押した場合など)。</summary>
    public event EventHandler<CollectionMembershipChangedEventArgs>? MembershipChanged;

    public GameDetailOverlay()
    {
        this.InitializeComponent();
        Visibility = Visibility.Collapsed;

        // 表示/非表示のフェードイン・フェードアウト(CollectionEditOverlayと共通)。
        OverlayFade.Attach(this);

        // ウィンドウの大きさを変えたとき、左の列が半透明に見えることがあった。色をリソース任せにせず、変わるたびに塗り直す。
        ScrimRoot.SizeChanged += (_, _) => ApplyPanelColors();
        ActualThemeChanged += (_, _) => ApplyPanelColors();
    }

    // ------------------------------------------------------------------
    // 表示/非表示
    // ------------------------------------------------------------------

    /// <summary>連結アニメーションの名前(開く時/閉じる時)。</summary>
    private const string ConnectedOpenKey = "GameCoverOpen";
    private const string ConnectedCloseKey = "GameCoverClose";

    /// <summary>
    /// 閉じる時に、カバー画像が戻る先(一覧側のカバー画像要素)を返す関数。
    /// 使う側のページが設定する。nullを返す(=画面外等)と、アニメーション無しで閉じる。
    /// </summary>
    public Func<RomFile, UIElement?>? ReturnTargetResolver { get; set; }

    private bool _openAnimationPending;

    /// <param name="sourceElement">一覧側のカバー画像。渡すと、そこから詳細のカバーへ連結アニメーションで移動する。</param>
    public void Show(RomFile game, UIElement? sourceElement = null, bool showLaunch = false)
    {
        // アプリ設定のテーマがダークなら暗い配色(Detail*Brush)に切り替える。標準は従来の明るい配色。
        ScrimRoot.RequestedTheme = AppPreferencesStore.Load().Theme == AppThemes.Dark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyPanelColors();
        if (!_switchingVariant)
        {
            IReadOnlyList<RomFile>? found = showLaunch ? VariantsResolver?.Invoke(game) : null;
            _variants = found is { Count: >= 2 } ? found : Array.Empty<RomFile>();
        }
        _variantVisible = _variants.Count >= 2;
        _variantFocused = false;
        _discMode = false;
        DiscIcon.Visibility = Visibility.Collapsed;
        FloppyIcon.Visibility = Visibility.Collapsed;
        CartIcon.Visibility = Visibility.Collapsed;
        _discNames = new List<string>();
        _discIndex = 0;
        EmulatorLauncher.Views.GamePlayPage.InitialDiscIndex = 0;
        VariantBox.Visibility = _variantVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_variantVisible)
        {
            int at = IndexOfVariant(game);
            VariantCaptionText.Text = $"バージョン {at + 1}/{_variants.Count}　◀ ▶";
            CartIcon.Visibility = Visibility.Visible;
            VariantNameText.Text = game.DisplayTitle;
        }
        CurrentGame = game;
        if (showLaunch && !_variantVisible && game.IsDiscSet && GameLibraryFacade.SupportsStartDiscChoice(game.ConsoleId))
            _ = LoadDiscChoicesAsync(game);
        _launchVisible = showLaunch;
        _launchFocused = showLaunch;
        _resumeVisible = false;
        _resumeFocused = false;
        ResumeBox.Visibility = Visibility.Collapsed;
        LaunchBox.Visibility = showLaunch ? Visibility.Visible : Visibility.Collapsed;
        UpdateLaunchDisplay();

        ConnectedAnimation? openAnimation = null;
        if (sourceElement is not null)
        {
            try
            {
                openAnimation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(ConnectedOpenKey, sourceElement);
            }
            catch { openAnimation = null; }
        }

        // 「コレクションに追加」欄はこの後LoadDetailCollectionsAsyncが非同期で読み込むため、
        // 読み込みが終わるまでは未フォーカス扱いにしておく。前回のチップも消しておく
        // (読み込み中に前のゲームのチップが一瞬見えてしまうのを防ぐ)。
        _chipIndex = -1;
        DetailCollections.Clear();
        _allCollectionItems.Clear();
        SetupLauncherChoices(game);
        SetupButtonsDisplay(game);

        FillGameInfo(game);

        // ステート画像は非同期で探す。見つかるまでは出さない(前のゲームの画像が残らないよう消しておく)。
        StateImages.Clear();
        _statesFocused = false;
        _stateIndex = 0;
        StateImageStrip.Visibility = Visibility.Collapsed;
        FitDescriptionLines();

        FillThumbnail(game);

        Visibility = Visibility.Visible;
        IsOpen = true;

        // 再開用の状態があれば「再開」ボタンを出す(ROMの場所の取得を伴うので、非同期で確認する)。
        // IsOpenをtrueにした後で呼ぶ(確認が同期的に終わっても「閉じている」と判定されないように)。
        _ = RefreshResumeAsync(game, focusResume: showLaunch);
        _ = LoadStateImagesAsync(game);

        if (openAnimation is not null) StartOpenAnimation(openAnimation);

        // 発火して待たない(WinUI3のUIスレッドにはDispatcherQueue経由のSynchronizationContextが
        // あるため、await後もUIスレッドへ自動的に戻ってくる)。
        _ = LoadDetailCollectionsAsync(game);
    }

    /// <summary>タイトル・発売元などの文字情報を表示する(開くときと、ゲーム情報の編集後に使う)。</summary>
    private void FillGameInfo(RomFile game)
    {
        DetailTitleText.Text = game.DisplayTitle;
        // データが無い項目(発売元が空、発売年が0など)は出さない。
        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(game.Publisher)) metaParts.Add(game.Publisher);
        if (game.ReleaseYear > 0) metaParts.Add($"{game.ReleaseYear}年発売");
        if (!string.IsNullOrWhiteSpace(game.Genre)) metaParts.Add($"ジャンル：{game.Genre}");
        DetailMetaText.Text = string.Join("　　", metaParts);
        DetailMetaText.Visibility = metaParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 「プレイ人数」と、記録済みのプレイ時間(遊んだことがあるときだけ)。
        DetailPlayersText.Text = game.PlayTimeMinutes > 0
            ? $"プレイ人数：{game.Players}　　プレイ時間：{FormatPlayTime(game.PlayTimeMinutes)}"
            : $"プレイ人数：{game.Players}";
        DetailDescriptionText.Text = game.Description;
    }

    private void FillThumbnail(RomFile game)
    {
        // 画像が無い/読み込みに失敗した場合のフォールバックとして、下地の色は常に表示しておく
        // (Imageが読み込めればその上に重なって隠れるので、フォールバックのVisibilityは
        // 切り替える必要が無い)。
        DetailThumbnailFallback.Background = (Microsoft.UI.Xaml.Media.Brush)HexConverter.Convert(game.AccentColorHex, typeof(object), null!, "");
        DetailThumbnailImage.Source = (Microsoft.UI.Xaml.Media.ImageSource?)IconConverter.Convert(game.IconPath, typeof(object), null!, "");
        // 画像が無いときだけ、アクセントカラーの単色を出す(画像があるときは、画像の左右に何も敷かない)。
        DetailThumbnailFallback.Visibility = string.IsNullOrEmpty(game.IconPath) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>左(画像)と右(文字)の列の背景を、不透明な単色で塗る(標準=白/薄グレー、ダーク=#262932/#17191F)。</summary>
    private void ApplyPanelColors()
    {
        bool dark = ScrimRoot.RequestedTheme == ElementTheme.Dark;
        DetailLeftPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            dark ? Windows.UI.Color.FromArgb(255, 0x26, 0x29, 0x32) : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        DetailRightPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            dark ? Windows.UI.Color.FromArgb(255, 0x17, 0x19, 0x1F) : Windows.UI.Color.FromArgb(255, 0xF5, 0xF6, 0xF8));
    }

    /// <summary>詳細のカバー画像が表示できる大きさになってから、連結アニメーションを開始する。</summary>
    private void StartOpenAnimation(ConnectedAnimation animation)
    {
        void Start()
        {
            try
            {
                DetailThumbnailImage.UpdateLayout();
                // 画像が無い/読み込み失敗時は、フォールバックの四角へ向かう。
                UIElement target = DetailThumbnailImage.Visibility == Visibility.Visible && DetailThumbnailImage.ActualWidth > 0
                    ? DetailThumbnailImage
                    : DetailThumbnailFallback.Visibility == Visibility.Visible ? DetailThumbnailFallback : DetailThumbnailImage;

                // 文字情報は、カバーが着地するのに合わせて動かす(連動アニメーション)。
                animation.TryStart(target, new UIElement[]
                {
                    DetailTitleText, DetailMetaText, DetailPlayersText, DetailDescriptionText,
                });
            }
            catch { /* アニメーションに失敗しても表示自体は成立している */ }
        }

        if (DetailThumbnailImage.Visibility == Visibility.Visible && DetailThumbnailImage.ActualWidth <= 0
            && DetailThumbnailFallback.Visibility != Visibility.Visible)
        {
            // 画像のデコード完了(サイズ確定)を待つ。失敗時は何もせずアニメーションを諦める。
            RoutedEventHandler? opened = null;
            ExceptionRoutedEventHandler? failed = null;
            opened = (_, _) => { DetailThumbnailImage.ImageOpened -= opened; DetailThumbnailImage.ImageFailed -= failed; Start(); };
            failed = (_, _) => { DetailThumbnailImage.ImageOpened -= opened; DetailThumbnailImage.ImageFailed -= failed; };
            DetailThumbnailImage.ImageOpened += opened;
            DetailThumbnailImage.ImageFailed += failed;
        }
        else
        {
            Start();
        }
    }

    public void Close()
    {
        if (!IsOpen) return;

        // 戻り先(一覧側のカバー)があれば、詳細のカバーからそこへ連結アニメーションで戻す。
        ConnectedAnimation? closeAnimation = null;
        UIElement? returnTarget = null;
        if (CurrentGame is not null && DetailThumbnailImage.ActualWidth > 0)
        {
            try
            {
                returnTarget = ReturnTargetResolver?.Invoke(CurrentGame);
                if (returnTarget is not null)
                    closeAnimation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(ConnectedCloseKey, DetailThumbnailImage);
            }
            catch { closeAnimation = null; }
        }

        Visibility = Visibility.Collapsed;
        GameControllerPage.Visibility = Visibility.Collapsed;
        if (GameEditor.IsOpen) GameEditor.Visibility = Visibility.Collapsed;
        IsOpen = false;
        CurrentGame = null;
        _chipIndex = -1;
        _launchFocused = false;
        _resumeFocused = false;
        _statesFocused = false;
        _variantFocused = false;
        _variants = Array.Empty<RomFile>();

        Closed?.Invoke(this, EventArgs.Empty);

        if (closeAnimation is not null && returnTarget is not null)
        {
            try { closeAnimation.TryStart(returnTarget); } catch { }
        }
    }

    /// <summary>要素の子孫から最初のImageを探す(一覧のカード内のカバー画像を取るために使う)。</summary>
    public static Image? FindFirstImage(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is Image self) return self;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            Image? found = FindFirstImage(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // ゲームパッド入力
    // ------------------------------------------------------------------

    /// <summary>表示中の入力。十字キーでチップのフォーカスを移動し、Aでフォーカス中のチップの
    /// 所属を切り替える。Bで閉じる。</summary>
    public void HandleGamepadInput(GamepadButton button)
    {
        // 複数枚(ディスク)の確認・お知らせが開いている間は、そちらへ入力を渡す。
        if (DiscConfirm.IsOpen)
        {
            DiscConfirm.HandleGamepadInput(button);
            return;
        }

        // ゲーム情報の編集画面が開いている間は、そちらへ入力を渡す。
        if (GameEditor.IsOpen)
        {
            GameEditor.HandleGamepadInput(button);
            return;
        }

        // ボタン割り当て変更の画面が開いている間は、そちらへ入力を渡す。
        if (GameControllerPage.Visibility == Visibility.Visible)
        {
            GameControllerPage.OnGamepadButton(button);
            return;
        }

        // 「ほかのコレクション」の選択画面が開いている間は、そちらへ入力を渡す。
        if (CollectionPicker.IsOpen)
        {
            CollectionPicker.HandleGamepadInput(button);
            return;
        }

        // 選択画面(ランチャー変更)が開いている間は、そちらへ入力を渡す。
        if (ButtonChoiceOverlay.IsOpen)
        {
            ButtonChoiceOverlay.HandleGamepadInput(button);
            return;
        }

        switch (button)
        {
            case GamepadButton.DPadLeft:
                if (_variantFocused) CycleVariant(-1);
                else if (_statesFocused) MoveStateFocus(-1);
                else if (!TopFocused) MoveChipFocus(-1);
                break;
            case GamepadButton.DPadRight:
                if (_variantFocused) CycleVariant(1);
                else if (_statesFocused) MoveStateFocus(1);
                else if (!TopFocused) MoveChipFocus(1);
                break;
            case GamepadButton.DPadUp:
                // ステート画像の列からは、コレクションの最後の行(無ければ「起動」等の上側の行)へ戻る。
                if (_statesFocused)
                {
                    LeaveStatesUp();
                }
                // 上へ: チップの先頭行 → 「起動」の順に移る。
                else if (FocusedTopRow is TopRow current)
                {
                    List<TopRow> rows = VisibleTopRows();
                    int index = rows.IndexOf(current);
                    if (index > 0) FocusTopRow(rows[index - 1]);
                }
                else if (DetailCollections.Count == 0 || _chipIndex < ChipColumns)
                {
                    List<TopRow> topRows = VisibleTopRows();
                    if (topRows.Count > 0) FocusTopRow(topRows[topRows.Count - 1]);
                }
                else
                {
                    MoveChipFocusVertical(-1);
                }
                break;
            case GamepadButton.DPadDown:
                if (FocusedTopRow is TopRow currentDown)
                {
                    List<TopRow> rows = VisibleTopRows();
                    int index = rows.IndexOf(currentDown);
                    if (index >= 0 && index < rows.Count - 1) FocusTopRow(rows[index + 1]);
                    else if (DetailCollections.Count == 0 && StateImages.Count > 0) SetStatesFocus(true);
                    else ClearTopFocus();
                }
                else if (_statesFocused) { /* いちばん下。動かない */ }
                else if (StateImages.Count > 0 && IsOnLastChipRow()) SetStatesFocus(true, ChipColumnOfFocus());
                else MoveChipFocusVertical(1);
                break;
            case GamepadButton.A:
                if (_variantFocused) _ = PickVariantAsync();
                else if (_resumeFocused) ResumeRequested?.Invoke(this, EventArgs.Empty);
                else if (_launchFocused) RaiseLaunch();
                else if (_statesFocused) ActivateFocusedState();
                else ToggleFocusedChip();
                break;
            case GamepadButton.X:
                // □ボタン: このゲームだけのボタン割り当てを編集する(カスタムが無ければ標準の状態から、
                // あればカスタムの内容を表示する)。
                EditGameButtons();
                break;
            case GamepadButton.Y:
                // △ボタン: ランチャーの選択画面を開く(ランチャーが複数あるときだけ)。
                if (_launcherOptions.Count >= 3) _ = ChangeLauncherAsync();
                break;
            case GamepadButton.Menu:
                // STARTボタン: ゲーム情報の編集画面を開く。
                OpenGameEditor();
                break;
            case GamepadButton.B:
                Close();
                break;
        }
    }

    /// <summary>パネルの外(暗幕の部分)をクリック/タップしたら閉じる(B=もどるのと同じ)。パネルの中や、重なっている別の画面での操作は対象外。</summary>
    private void ScrimRoot_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (!IsOpen) return;
        if (DiscConfirm.IsOpen || GameEditor.IsOpen || GameControllerPage.Visibility == Visibility.Visible
            || CollectionPicker.IsOpen || ButtonChoiceOverlay.IsOpen) return;

        // クリックされた要素がパネル/操作ガイドの中なら、閉じない。
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null)
        {
            // 重なっている別の画面(選択画面など)での操作は、その画面が閉じた後にここへ届くことがあるので、
            // 閉じている/いないに関わらず、それらの中でのクリックは対象外にする。
            if (ReferenceEquals(node, DetailPanelRoot) || ReferenceEquals(node, DetailHintBar)
                || ReferenceEquals(node, ButtonChoiceOverlay) || ReferenceEquals(node, CollectionPicker)
                || ReferenceEquals(node, GameControllerPage) || ReferenceEquals(node, GameEditor)
                || ReferenceEquals(node, DiscConfirm)) return;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }

        e.Handled = true;
        Close();
    }

    private void OpenGameEditor()
    {
        RomFile? game = CurrentGame;
        if (game is null || game.No <= 0 || game.IsBiosBoot) return;
        if (GameEditor.IsOpen) return;

        GameEditor.Show(game);
    }

    // ==================================================================
    // 複数枚(ディスク)のまとめ/解除(ゲーム情報の編集画面の「複数枚(ディスク)」から)
    // ==================================================================

    private void GameEditor_DiscSetRequested(object? sender, EventArgs e)
    {
        RomFile? game = CurrentGame;
        if (game is null || !IsOpen || game.No <= 0) return;

        _ = RunDiscSetAsync(game);
    }

    private async Task RunDiscSetAsync(RomFile game)
    {
        if (ButtonChoiceOverlay.IsOpen || DiscConfirm.IsOpen) return;

        try
        {
            if (game.IsDiscSet) await RemoveDiscSetAsync(game);
            else await CreateDiscSetAsync(game);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameDetailOverlay] 複数枚のまとめ/解除に失敗しました: {ex.Message}");
            await DiscConfirm.AlertAsync("複数枚のまとめ/解除に失敗しました。\n" + ex.Message);
        }
    }

    /// <summary>
    /// このゲームを1枚目にして、2枚目以降を1枚ずつ選んでもらい(選んだ順がディスクの順)、.m3uでまとめる。
    /// 「ここで完了」で書き出す。途中でB(キャンセル)すれば、何も作らずに戻る。
    /// </summary>
    private async Task CreateDiscSetAsync(RomFile first)
    {
        IReadOnlyList<RomFile> candidates = await GameLibraryFacade.GetDiscSetCandidatesAsync(first);
        if (!IsOpen || !ReferenceEquals(CurrentGame, first)) return;

        if (candidates.Count == 0)
        {
            await DiscConfirm.AlertAsync("まとめられるゲームが見つかりません。\n\n同じフォルダに、このゲームのほかのディスク(まだまとめていないもの)が必要です。");
            return;
        }

        var chosen = new List<RomFile>();
        while (true)
        {
            List<RomFile> remaining = candidates.Where(c => !chosen.Contains(c)).ToList();
            if (remaining.Count == 0) break;

            var items = new List<GamepadPickerItem>();
            if (chosen.Count > 0)
            {
                items.Add(new GamepadPickerItem { Label = $"ここで完了({chosen.Count + 1}枚をまとめる)", Tag = -1 });
            }
            for (int i = 0; i < remaining.Count; i++)
            {
                items.Add(new GamepadPickerItem { Label = remaining[i].FileName, Tag = i });
            }

            string title = chosen.Count == 0
                ? $"2枚目のディスクを選ぶ(1枚目: {first.FileName})"
                : $"{chosen.Count + 2}枚目のディスクを選ぶ(いま{chosen.Count + 1}枚)";
            GamepadPickerItem? picked = await ButtonChoiceOverlay.ShowAsync(title, items);
            if (!IsOpen || !ReferenceEquals(CurrentGame, first)) return;
            if (picked is null) return;   // B: 何も作らずに戻る
            if (picked.Tag is not int index) return;
            if (index < 0) break;         // ここで完了

            chosen.Add(remaining[index]);
        }

        if (chosen.Count == 0) return;

        DiscSetResult result = await GameLibraryFacade.CreateDiscSetAsync(first, chosen);
        if (!result.Success)
        {
            await DiscConfirm.AlertAsync("複数枚をまとめられませんでした。\n" + result.Message);
            return;
        }

        // まとめた(1枚目も隠れる)ので、一覧を読み直してもらい、この詳細画面は閉じる。
        // 一覧では、まとめてできた.m3uのゲームにフォーカスしてもらう。
        DiscSetFocusNo = result.Set?.No;
        DiscSetChanged?.Invoke(this, EventArgs.Empty);
        Close();
    }

    /// <summary>複数枚のまとめを解除する(.m3uを削除し、ディスクは一覧へ戻る)。</summary>
    private async Task RemoveDiscSetAsync(RomFile set)
    {
        bool ok = await DiscConfirm.ShowAsync(
            $"「{set.DisplayTitle}」のまとめを解除します。\n\n.m3uファイル({set.FileName})を削除し、ディスクは1枚ずつ一覧に戻ります。ディスクのファイルは消えません。",
            "解除する");
        if (!ok || !IsOpen || !ReferenceEquals(CurrentGame, set)) return;

        DiscSetResult result = await GameLibraryFacade.RemoveDiscSetAsync(set);
        if (!result.Success)
        {
            await DiscConfirm.AlertAsync("まとめを解除できませんでした。\n" + result.Message);
            return;
        }

        DiscSetFocusNo = result.FirstMemberNo;   // 解除後は、元の1枚目のディスクにフォーカス
        DiscSetChanged?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void GameEditor_Closed(object? sender, bool saved)
    {
        RomFile? game = CurrentGame;
        if (!saved || game is null || !IsOpen) return;

        FillGameInfo(game);
        FillThumbnail(game);
        if (_variantVisible) VariantNameText.Text = game.DisplayTitle;
        GameEdited?.Invoke(this, game);
    }

    // 画面の上側にある、フォーカスできる行(上から順)。
    private enum TopRow
    {
        Resume,
        Launch,
        Variant,
    }

    private TopRow? FocusedTopRow =>
        _resumeFocused ? TopRow.Resume
        : _launchFocused ? TopRow.Launch
        : _variantFocused ? TopRow.Variant
        : null;

    /// <summary>今表示されている行(上から順)。起動とランチャー変更は条件つきで表示される。</summary>
    private List<TopRow> VisibleTopRows()
    {
        var rows = new List<TopRow>();
        if (_resumeVisible) rows.Add(TopRow.Resume);
        if (_launchVisible) rows.Add(TopRow.Launch);
        if (_variantVisible) rows.Add(TopRow.Variant);
        return rows;
    }

    private void FocusTopRow(TopRow row)
    {
        switch (row)
        {
            case TopRow.Resume: SetResumeFocus(true); break;
            case TopRow.Launch: SetLaunchFocus(true); break;
            case TopRow.Variant: SetVariantFocus(true); break;
        }
    }

    /// <summary>上側の行のフォーカスをすべて外す(チップ側へ移る)。</summary>
    private void ClearTopFocus()
    {
        _launchFocused = false;
        _resumeFocused = false;
        _statesFocused = false;
        _variantFocused = false;
        RefreshTopFocusDisplay();
    }

    /// <summary>ステート画像の列へフォーカスを移す(コレクション欄の下にある)。index指定があればその位置から。</summary>
    private void SetStatesFocus(bool focused, int index = -1)
    {
        _statesFocused = focused && StateImages.Count > 0;
        if (_statesFocused)
        {
            _launchFocused = false;
            _resumeFocused = false;
            _variantFocused = false;
            if (index >= 0) _stateIndex = index;
            _stateIndex = Math.Clamp(_stateIndex, 0, StateImages.Count - 1);
        }
        RefreshTopFocusDisplay();
    }

    private bool IsOnLastChipRow()
        => DetailCollections.Count == 0 || (_chipIndex >= 0 && _chipIndex / ChipColumns >= (DetailCollections.Count - 1) / ChipColumns);

    private int ChipColumnOfFocus() => _chipIndex < 0 ? 0 : _chipIndex % ChipColumns;

    /// <summary>ステート画像の列から上へ。コレクションがあれば最後の行の同じ列(無ければ末尾)へ、無ければ上側の行へ。</summary>
    private void LeaveStatesUp()
    {
        _statesFocused = false;
        int count = DetailCollections.Count;
        if (count > 0)
        {
            int lastRowStart = (count - 1) / ChipColumns * ChipColumns;
            int target = Math.Min(lastRowStart + Math.Min(_stateIndex, ChipColumns - 1), count - 1);
            SetChipFocus(target);
            RefreshTopFocusDisplay();
            return;
        }
        List<TopRow> rows = VisibleTopRows();
        if (rows.Count > 0) FocusTopRow(rows[rows.Count - 1]);
        else RefreshTopFocusDisplay();
    }

    private void MoveStateFocus(int delta)
    {
        if (StateImages.Count == 0) return;
        _stateIndex = GamepadListNavigator.Move(_stateIndex, delta, StateImages.Count, wrap: false);
        RefreshTopFocusDisplay();
    }

    private void ActivateFocusedState()
    {
        if (_stateIndex < 0 || _stateIndex >= StateImages.Count) return;
        StateResumeRequested?.Invoke(this, StateImages[_stateIndex].Key);
    }

    /// <summary>拡大が画像の中心を軸にするよう、大きさが決まったら中心点を合わせる。</summary>
    private void StateTile_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not UIElement element) return;
        Microsoft.UI.Composition.Visual visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new System.Numerics.Vector3((float)e.NewSize.Width / 2f, (float)e.NewSize.Height / 2f, 0f);
    }

    // 画像のフォーカスが変わったら、その画像だけ少し拡大/縮小する(コンポジションのアニメーション)。
    private readonly Dictionary<FrameworkElement, System.ComponentModel.PropertyChangedEventHandler> _tileHandlers = new();

    private void StateTile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not StateImageItem item || _tileHandlers.ContainsKey(element)) return;

        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(StateImageItem.Scale)) AnimateTileScale(element, item.Scale);
        };
        _tileHandlers[element] = handler;
        item.PropertyChanged += handler;
        AnimateTileScale(element, item.Scale);
    }

    private void StateTile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || !_tileHandlers.Remove(element, out var handler)) return;
        if (element.Tag is StateImageItem item) item.PropertyChanged -= handler;
    }

    private static void AnimateTileScale(UIElement element, System.Numerics.Vector3 target)
    {
        Microsoft.UI.Composition.Visual visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, target);
        animation.Duration = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation("Scale", animation);
    }

    private void StateTile_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StateImageItem item }) return;
        int index = StateImages.IndexOf(item);
        if (index < 0) return;
        _stateIndex = index;
        SetStatesFocus(true);
        ActivateFocusedState();
    }

    // ------------------------------------------------------------------
    // 「コレクションに追加」チップ
    // ------------------------------------------------------------------

    /// <summary>
    /// 全コレクション(「お気に入り」含む)と、指定ゲームがどのコレクションに属しているかを
    /// DB(RomCollectionRepository)から読み込み、DetailCollectionsへトグル項目として反映する。
    /// </summary>
    private async Task LoadDetailCollectionsAsync(RomFile game)
    {
        List<RomCollectionInfo> allCollections;
        List<string> memberCollectionIds;
        try
        {
            allCollections = await CollectionLibraryFacade.GetAllAsync();
            memberCollectionIds = await CollectionLibraryFacade.GetCollectionIdsForRomFileAsync(game.No);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameDetailOverlay] コレクション情報の読み込みに失敗しました: {ex.Message}");
            allCollections = new List<RomCollectionInfo>();
            memberCollectionIds = new List<string>();
        }

        // 読み込み中にBボタン等で閉じたり、別のゲームの詳細を開き直したりしていた場合は、
        // 古い結果で表示を汚さないよう何もしない。
        if (!IsOpen || !ReferenceEquals(CurrentGame, game))
        {
            return;
        }

        _allCollectionItems.Clear();
        foreach (RomCollectionInfo collection in allCollections)
        {
            _allCollectionItems.Add(new CollectionChipItem
            {
                CollectionId = collection.Id,
                Name = collection.Id == CollectionLibraryFacade.FavoriteCollectionId
                    ? "♥ " + collection.Name
                    : collection.Name,
                IsMember = memberCollectionIds.Contains(collection.Id),
            });
        }
        RebuildCollectionChips();

        // 読み込み完了後、ゲームパッドのフォーカスを一覧の先頭チップへ合わせる。
        SetChipFocus(0);
    }

    /// <summary>
    /// 「コレクションに追加」欄のチップを作り直す。並びは「お気に入り」→このゲームが入っているもの→その他
    /// (それぞれ元の順)。MaxCollectionChipsを超えるぶんは「ほかのコレクション…」へ回す。
    /// 並べ替えは作り直したときだけ(チップを押して入れ替えても、その場では動かさない)。
    /// </summary>
    private void RebuildCollectionChips()
    {
        var ordered = _allCollectionItems
            .OrderBy(c => c.CollectionId == CollectionLibraryFacade.FavoriteCollectionId ? 0 : c.IsMember ? 1 : 2)
            .ToList();

        DetailCollections.Clear();
        if (ordered.Count <= MaxCollectionChips + 1)
        {
            foreach (var c in ordered) DetailCollections.Add(c);
        }
        else
        {
            foreach (var c in ordered.Take(MaxCollectionChips)) DetailCollections.Add(c);
            DetailCollections.Add(new CollectionChipItem { CollectionId = MoreChipId, Name = "ほかのコレクション…" });
        }
    }

    /// <summary>
    /// 「ほかのコレクション…」: 全コレクションを一覧から複数選んで、このゲームの所属をまとめて変える。
    /// 適用したら、変わったものだけDBへ反映し、チップを作り直す。Bで変更せずに戻る。
    /// </summary>
    private async Task OpenCollectionPickerAsync()
    {
        RomFile? game = CurrentGame;
        if (game is null || CollectionPicker.IsOpen) return;

        var rows = _allCollectionItems.Select(c => (c.CollectionId, c.Name, c.IsMember)).ToList();
        string[]? picked = await CollectionPicker.ShowAsync(
            "コレクションに追加",
            rows,
            "追加したいコレクションを、いくつでも選べます。選んだものにこのゲームが入り、外したものからは外れます。",
            "選んだコレクション",
            "なし");
        if (picked is null) return;

        var wanted = new HashSet<string>(picked);
        foreach (CollectionChipItem item in _allCollectionItems.ToList())
        {
            bool want = wanted.Contains(item.CollectionId);
            if (want != item.IsMember) await ApplyMembershipAsync(item, want);
        }

        // 待っている間に閉じた/別のゲームへ替わっていたら、表示は触らない。
        if (!IsOpen || !ReferenceEquals(CurrentGame, game)) return;
        RebuildCollectionChips();
        SetChipFocus(DetailCollections.Count - 1);
    }

    /// <summary>
    /// チップがマウスでクリックされた。
    ///
    /// Clickはユーザーが実際に押した時にしか発火しない(バインディングによるIsCheckedの変化や、
    /// コンテナ再利用時のデータ割り当て直しでは発火しない)ので、Tagは確実にクリックされた
    /// チップ自身を指している。
    ///
    /// ToggleButtonはクリックされた時点で自分のIsCheckedを勝手に反転させているが、表示の正は
    /// あくまでitem.IsMember(DBの状態)とする。いったんIsCheckedをIsMemberへ戻してから
    /// DB更新を行い、成功した場合だけIsMemberの変化→OneWayバインディングで表示が切り替わる。
    /// </summary>
    private async void CollectionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.Tag is not CollectionChipItem item)
        {
            return;
        }

        bool wantMember = !item.IsMember;
        toggle.IsChecked = item.IsMember;

        int index = DetailCollections.IndexOf(item);
        if (index >= 0) SetChipFocus(index);

        if (item.CollectionId == MoreChipId)
        {
            await OpenCollectionPickerAsync();
            return;
        }

        await ApplyMembershipAsync(item, wantMember);
    }

    /// <summary>
    /// ゲームパッドのAボタンで、フォーカス中のチップの所属状態を反転させる。ToggleButton自体には
    /// 触れず、IsMemberとDBだけを更新する(IsCheckedはOneWayバインディングで自動的に追従する)。
    /// 失敗時はIsMemberを変更しないので、表示も元のまま残る。
    /// </summary>
    private async void ToggleFocusedChip()
    {
        if (_chipIndex < 0 || _chipIndex >= DetailCollections.Count)
        {
            return;
        }

        CollectionChipItem item = DetailCollections[_chipIndex];
        if (item.CollectionId == MoreChipId)
        {
            await OpenCollectionPickerAsync();
            return;
        }
        await ApplyMembershipAsync(item, !item.IsMember);
    }

    /// <summary>
    /// 表示中のゲームについて、指定コレクションへの所属をwantMemberの状態に合わせてDBへ反映し、
    /// 成功したらitem.IsMemberを更新して<see cref="MembershipChanged"/>を発火する。
    /// 成功すればtrue、コレクションが見つからない等で反映できなければfalseを返す。
    ///
    /// 対象ゲームは呼び出し時点のCurrentGameで確定させる(await中にオーバーレイが閉じられても、
    /// DBへの反映と通知は正しいゲームに対して行う)。
    /// </summary>
    private async Task<bool> ApplyMembershipAsync(CollectionChipItem item, bool wantMember)
    {
        RomFile? game = CurrentGame;
        if (game is null)
        {
            return false;
        }

        if (!_applyingChips.Add(item))
        {
            // 同じチップの前回の更新がまだ終わっていない。
            return false;
        }

        bool ok;
        try
        {
            if (wantMember)
            {
                // 追加時はコレクション自体が見つからなかった(削除された等)場合にfalseが返る。
                ok = await CollectionLibraryFacade.AddToCollectionAsync(item.CollectionId, game.No);
            }
            else
            {
                // 削除は「元々入っていなければ何もしない」で構わないので、実際に削除できた
                // 行数に関わらず常に成功扱いにする(0件でも失敗ではない)。
                await CollectionLibraryFacade.RemoveFromCollectionAsync(item.CollectionId, game.No);
                ok = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[GameDetailOverlay] コレクション更新に失敗しました({item.CollectionId}): {ex.Message}");
            ok = false;
        }
        finally
        {
            _applyingChips.Remove(item);
        }

        if (ok)
        {
            item.IsMember = wantMember;
            MembershipChanged?.Invoke(this, new CollectionMembershipChangedEventArgs
            {
                Game = game,
                CollectionId = item.CollectionId,
                IsMember = wantMember,
            });
        }

        return ok;
    }

    /// <summary>十字キーでチップのフォーカスをdelta分動かす(端で折り返す)。</summary>
    private void MoveChipFocus(int delta)
    {
        if (DetailCollections.Count == 0) return;

        int current = _chipIndex < 0 ? 0 : _chipIndex;
        int next = GamepadListNavigator.Move(current, delta, DetailCollections.Count, wrap: true);
        SetChipFocus(next);
    }

    /// <summary>チップの列数(XAMLのItemsWrapGrid.MaximumRowsOrColumnsと同じ値にすること)。</summary>
    private const int ChipColumns = 3;

    /// <summary>十字キーの上下で、チップのフォーカスを1行ぶん動かす。端(最初の行/最後の行)では動かない。
    /// 最後の行が途中までしかない場合、その1つ上の行の右端から下へ動くと、最後のチップへ移る。</summary>
    private void MoveChipFocusVertical(int direction)
    {
        int count = DetailCollections.Count;
        if (count == 0) return;

        int current = _chipIndex < 0 ? 0 : _chipIndex;
        int lastRow = (count - 1) / ChipColumns;
        int row = current / ChipColumns;
        int target = current + direction * ChipColumns;

        if (direction < 0)
        {
            if (row == 0) return;
        }
        else
        {
            if (row >= lastRow) return;
            if (target >= count) target = count - 1;
        }
        SetChipFocus(target);
    }

    private void SetChipFocus(int index)
    {
        if (DetailCollections.Count == 0)
        {
            _chipIndex = -1;
            return;
        }

        index = Math.Clamp(index, 0, DetailCollections.Count - 1);
        for (int i = 0; i < DetailCollections.Count; i++)
        {
            DetailCollections[i].IsFocused = !TopFocused && i == index;
        }
        _chipIndex = index;
    }
    // ------------------------------------------------------------------
    // 「ランチャー変更」(ゲーム別のランチャー指定。RomFile.Launcherへ保存)
    // ------------------------------------------------------------------

    /// <summary>表示するゲームのコンソールに属するランチャーを集め、現在の指定を反映する。
    /// ランチャーが2つ未満なら選ぶ余地が無いので欄ごと隠す。</summary>
    private void SetupLauncherChoices(RomFile game)
    {
        _launcherOptions.Clear();
        _launcherOptions.Add(null);

        ConsoleInfo? console = SampleData.GetConsoles().FirstOrDefault(c => c.Id == game.ConsoleId);
        _defaultLauncherName = console?.LauncherName ?? string.Empty;

        // コア(DLL)や外部プログラムが実際に存在するランチャーだけを選択肢にする。
        foreach (LauncherInfo l in SampleData.GetLaunchers().Where(l => l.Id == game.ConsoleId && IsLauncherAvailable(l)))
        {
            _launcherOptions.Add(l);
        }

        // 指定済みのランチャーが今は無い(削除された等)場合は、既定扱いで表示する。
        _launcherIndex = string.IsNullOrEmpty(game.Launcher)
            ? 0
            : Math.Max(0, _launcherOptions.FindIndex(o => o is not null && o.Name == game.Launcher));

        UpdateLauncherDisplay();
    }

    /// <summary>ランチャーが使える状態か。libretroコアならDLLが存在すること、外部プログラムなら
    /// 実行ファイルが存在する(PATH上のコマンド名ならPATHから見つかる)こと。</summary>
    private static bool IsLauncherAvailable(LauncherInfo launcher)
    {
        try
        {
            if (!launcher.IsExternal)
            {
                string core = SampleData.ResolveCorePath(launcher.CorePath ?? string.Empty);
                return !string.IsNullOrEmpty(core) && System.IO.File.Exists(core);
            }

            string exe = SampleData.ResolveExePath(launcher.ExePath ?? string.Empty);
            if (string.IsNullOrEmpty(exe)) return false;
            if (System.IO.Path.IsPathRooted(exe)) return System.IO.File.Exists(exe);

            // 区切りを含まない名前: PATH上を探す(拡張子が無ければ.exeも試す)。
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathVar.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = System.IO.Path.Combine(dir.Trim().Trim('"'), exe);
                if (System.IO.File.Exists(candidate) || System.IO.File.Exists(candidate + ".exe")) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>このゲームだけのランチャー指定があれば「カスタムランチャー」の印を出し、
    /// ランチャーが複数あるときだけ操作ガイドに「△ ランチャー変更」を出す。</summary>
    private void UpdateLauncherDisplay()
    {
        CustomLauncherBadge.Visibility = _launcherIndex > 0 && _launcherIndex < _launcherOptions.Count
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool canChange = _launcherOptions.Count >= 3;
        bool shown = DetailHintBar.Hints.Contains(_launcherHint);
        if (canChange && !shown) DetailHintBar.Hints.Insert(Math.Max(0, DetailHintBar.Hints.Count - 1), _launcherHint);
        else if (!canChange && shown) DetailHintBar.Hints.Remove(_launcherHint);
    }

    private readonly HintDefinition _launcherHint = new() { Key = "Y", Label = "ランチャー変更" };

    private void SetLaunchFocus(bool focused)
    {
        _launchFocused = focused;
        if (focused) { _resumeFocused = false; _statesFocused = false; _variantFocused = false; }
        RefreshTopFocusDisplay();
    }

    private void SetVariantFocus(bool focused)
    {
        _variantFocused = focused && _variantVisible;
        if (_variantFocused) { _launchFocused = false; _resumeFocused = false; _statesFocused = false; }
        RefreshTopFocusDisplay();
    }

    private int IndexOfVariant(RomFile game)
    {
        for (int i = 0; i < _variants.Count; i++)
            if (ReferenceEquals(_variants[i], game) || _variants[i].No == game.No) return i;
        return 0;
    }

    /// <summary>別のバージョンへ切り替える。表示(タイトル・カバー・ステート等)をそのバージョンのものに作り直し、
    /// バージョン欄のフォーカスは保つ。</summary>
    private void SwitchVariant(RomFile variant, int direction = 0)
    {
        if (CurrentGame is null || ReferenceEquals(variant, CurrentGame)) return;
        if (direction == 0) direction = IndexOfVariant(variant) >= IndexOfVariant(CurrentGame) ? 1 : -1;
        _switchingVariant = true;
        try { Show(variant, null, showLaunch: true); }
        finally { _switchingVariant = false; }
        SetVariantFocus(true);
        SlideCover(direction);
        AnimateVariantBoxChange(direction, spinDisc: false);
    }

    /// <summary>バージョンを切り替えたとき、新しいカバー画像を左右(direction: 1=右から、-1=左から)から滑り込ませる。</summary>
    private void SlideCover(int direction)
    {
        try
        {
            float width = (float)Math.Max(DetailThumbnailImage.ActualWidth, 240);
            foreach (UIElement el in new UIElement[] { DetailThumbnailImage, DetailThumbnailFallback })
            {
                if (el.Visibility != Visibility.Visible) continue;
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(el, true);
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
                var compositor = visual.Compositor;
                var ease = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.2f, 1f));

                var slide = compositor.CreateVector3KeyFrameAnimation();
                slide.InsertKeyFrame(0f, new System.Numerics.Vector3(direction * width * 0.7f, 0, 0));
                slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
                slide.Duration = TimeSpan.FromMilliseconds(260);
                visual.StartAnimation("Translation", slide);

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f, ease);
                fade.Duration = TimeSpan.FromMilliseconds(260);
                visual.StartAnimation("Opacity", fade);
            }
        }
        catch { /* 演出に失敗しても切り替え自体は成立している */ }
    }

    /// <summary>
    /// 複数枚組(.m3u)のとき、ディスクの一覧を読み、2枚以上あればバージョン欄を「ディスク n/N」として出す。
    /// 左右または一覧から選ぶと、そのディスクを最初に入れた状態で起動する(選んだ番号は起動の直前にGamePlayPageへ渡す)。
    /// </summary>
    private async Task LoadDiscChoicesAsync(RomFile game)
    {
        // Show()の途中から呼ばれる。DBの読み込みが同期的に終わると、Show()が終わる前(IsOpenがtrueになる前)に
        // 続きが走ってしまうため、いったんUIスレッドへ手番を返して、Show()が済んでから進める。
        await Task.Yield();

        IReadOnlyList<string> names;
        try { names = await GameLibraryFacade.GetDiscNamesAsync(game); }
        catch { return; }

        if (names.Count < 2 || !IsOpen || !ReferenceEquals(CurrentGame, game) || _variantVisible) return;

        _discMode = true;
        _discNames = names.ToList();
        _discIndex = 0;
        _variantVisible = true;
        VariantBox.Visibility = Visibility.Visible;
        // MSX/MSX2はフロッピー、それ以外は光ディスクのアイコン。
        _discIsFloppy = GameLibraryFacade.IsFloppyMedia(game.ConsoleId);
        DiscIcon.Visibility = _discIsFloppy ? Visibility.Collapsed : Visibility.Visible;
        FloppyIcon.Visibility = _discIsFloppy ? Visibility.Visible : Visibility.Collapsed;
        CartIcon.Visibility = Visibility.Collapsed;
        UpdateDiscBox();
        RefreshTopFocusDisplay();
    }

    private void UpdateDiscBox()
    {
        VariantCaptionText.Text = $"ディスク {_discIndex + 1}/{_discNames.Count}　◀ ▶";
        VariantNameText.Text = _discNames[_discIndex];
    }

    private void CycleDisc(int delta)
    {
        if (_discNames.Count < 2) return;
        _discIndex = (_discIndex + delta + _discNames.Count) % _discNames.Count;
        UpdateDiscBox();
        AnimateDiscBoxChange(delta > 0 ? 1 : -1);
    }

    /// <summary>
    /// ディスク/バージョンを替えたとき、名前を左右(direction: 1=右から、-1=左から)から滑り込ませる。
    /// spinDisc=trueなら光ディスクのアイコンを1回転させ、falseならROMカセットのアイコンを同じ向きから滑り込ませる。
    /// 演出に失敗しても、切り替え自体は済んでいる。
    /// </summary>
    private void AnimateDiscBoxChange(int direction)
    {
        // フロッピー(MSX/MSX2)は回さず、差し込むように滑り込ませる。光ディスクは回す。
        if (_discIsFloppy) AnimateVariantBoxChange(direction, spinDisc: false, slideIcon: FloppyIcon);
        else AnimateVariantBoxChange(direction, spinDisc: true);
    }

    private void AnimateVariantBoxChange(int direction, bool spinDisc, UIElement? slideIcon = null)
    {
        try
        {
            foreach (UIElement el in new UIElement[] { VariantCaptionText, VariantNameText })
            {
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(el, true);
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
                var compositor = visual.Compositor;
                var ease = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.2f, 1f));

                var slide = compositor.CreateVector3KeyFrameAnimation();
                slide.InsertKeyFrame(0f, new System.Numerics.Vector3(direction * 70f, 0, 0));
                slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
                slide.Duration = TimeSpan.FromMilliseconds(260);
                visual.StartAnimation("Translation", slide);

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f, ease);
                fade.Duration = TimeSpan.FromMilliseconds(260);
                visual.StartAnimation("Opacity", fade);
            }

            if (!spinDisc)
            {
                // ROMカセット/フロッピーは、進む向きの反対側から差し込まれるように滑り込む。
                UIElement icon = slideIcon ?? CartIcon;
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(icon, true);
                var cart = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
                var cartEase = cart.Compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.2f, 1f));
                var cartSlide = cart.Compositor.CreateVector3KeyFrameAnimation();
                cartSlide.InsertKeyFrame(0f, new System.Numerics.Vector3(direction * 36f, 0, 0));
                cartSlide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, cartEase);
                cartSlide.Duration = TimeSpan.FromMilliseconds(300);
                cart.StartAnimation("Translation", cartSlide);
                return;
            }

            // 光ディスクは、進む向きに1回転する(見た目は元の角度へ戻るので、次の回転も同じ式で始められる)。
            var disc = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(DiscIcon);
            disc.CenterPoint = new System.Numerics.Vector3((float)(DiscIcon.ActualWidth / 2), (float)(DiscIcon.ActualHeight / 2), 0);
            var spinEase = disc.Compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.2f, 1f));
            var spin = disc.Compositor.CreateScalarKeyFrameAnimation();
            spin.InsertKeyFrame(0f, 0f);
            spin.InsertKeyFrame(1f, direction * 360f, spinEase);
            spin.Duration = TimeSpan.FromMilliseconds(420);
            disc.StartAnimation("RotationAngleInDegrees", spin);
        }
        catch { /* 演出に失敗しても切り替え自体は成立している */ }
    }

    private async Task PickDiscAsync()
    {
        RomFile? game = CurrentGame;
        if (game is null || _discNames.Count < 2 || ButtonChoiceOverlay.IsOpen) return;

        var items = new List<GamepadPickerItem>();
        for (int i = 0; i < _discNames.Count; i++)
            items.Add(new GamepadPickerItem { Label = _discNames[i], Tag = i, IsChecked = i == _discIndex });

        GamepadPickerItem? picked = await ButtonChoiceOverlay.ShowAsync("最初に入れるディスクを選ぶ", items);
        if (picked?.Tag is not int next || !IsOpen || !ReferenceEquals(CurrentGame, game)) return;
        if (next >= 0 && next < _discNames.Count && next != _discIndex)
        {
            int direction = next > _discIndex ? 1 : -1;
            _discIndex = next;
            UpdateDiscBox();
            AnimateDiscBoxChange(direction);
        }
    }

    /// <summary>「起動」を通知する。ディスクを選んでいれば、その番号を起動前にGamePlayPageへ渡す。</summary>
    private void RaiseLaunch()
    {
        EmulatorLauncher.Views.GamePlayPage.InitialDiscIndex = _discMode ? _discIndex : 0;
        LaunchRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CycleVariant(int delta)
    {
        if (_discMode) { CycleDisc(delta); return; }
        if (_variants.Count < 2 || CurrentGame is null) return;
        int next = (IndexOfVariant(CurrentGame) + delta + _variants.Count) % _variants.Count;
        SwitchVariant(_variants[next], delta > 0 ? 1 : -1);
    }

    private async Task PickVariantAsync()
    {
        if (_discMode) { await PickDiscAsync(); return; }

        RomFile? game = CurrentGame;
        if (game is null || _variants.Count < 2 || ButtonChoiceOverlay.IsOpen) return;

        int current = IndexOfVariant(game);
        var items = new List<GamepadPickerItem>();
        for (int i = 0; i < _variants.Count; i++)
            items.Add(new GamepadPickerItem { Label = _variants[i].DisplayTitle, Tag = i, IsChecked = i == current });

        GamepadPickerItem? picked = await ButtonChoiceOverlay.ShowAsync("バージョンを選ぶ", items);
        if (picked?.Tag is not int next || !IsOpen || !ReferenceEquals(CurrentGame, game)) return;
        if (next >= 0 && next < _variants.Count) SwitchVariant(_variants[next]);
    }

    private void VariantBox_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SetVariantFocus(true);
        _ = PickVariantAsync();
    }

    private void SetResumeFocus(bool focused)
    {
        _resumeFocused = focused;
        if (focused) { _launchFocused = false; _statesFocused = false; _variantFocused = false; }
        RefreshTopFocusDisplay();
    }

    /// <summary>
    /// このゲームに再開用の状態(前回「タイトル選択にもどる」で保存したもの)があれば、「再開」ボタンを出す。
    /// 「ゲーム起動方法」が「詳細画面から起動」のときは、開いた直後のフォーカスを「再開」へ移す。
    /// 「そのまま起動」のときは、起動ボタンも合わせて出し(フォーカスはしない)、詳細画面からも選べるようにする。
    /// </summary>
    private async Task RefreshResumeAsync(RomFile game, bool focusResume)
    {
        bool exists = false;
        try
        {
            string? corePath = ResolveCorePathFor(game);
            if (!string.IsNullOrEmpty(corePath))
            {
                string? romPath = game.IsBiosBoot ? string.Empty : await GameLibraryFacade.ResolveFullPathAsync(game);
                exists = romPath is not null && SuspendState.Exists(corePath, romPath);
                System.Diagnostics.Debug.WriteLine(
                    $"[GameDetailOverlay] 再開用ステート確認: core={corePath} rom={romPath} folder={(romPath is null ? "(なし)" : SuspendState.GetFolder(corePath, romPath))} exists={exists}");
            }
        }
        catch { exists = false; }

        // 待っている間に閉じた/別のゲームへ替わっていたら何もしない。
        if (!exists || !IsOpen || !ReferenceEquals(CurrentGame, game)) return;

        _resumeVisible = true;
        ResumeBox.Visibility = Visibility.Visible;
        if (!_launchVisible)
        {
            _launchVisible = true;
            LaunchBox.Visibility = Visibility.Visible;
        }
        if (focusResume && _launchFocused)
        {
            _launchFocused = false;
            _resumeFocused = true;
        }
        RefreshTopFocusDisplay();
    }

    /// <summary>
    /// このゲームのステートセーブ画像(中断→新しい順、最大4枚)を探して、説明文の下に並べる。
    /// 1枚も無ければ何も出さない。表示専用で、フォーカスや操作の対象にはしない。
    /// </summary>
    private async Task LoadStateImagesAsync(RomFile game)
    {
        try
        {
            string? corePath = ResolveCorePathFor(game);
            if (string.IsNullOrEmpty(corePath)) return;
            string? romPath = game.IsBiosBoot ? string.Empty : await GameLibraryFacade.ResolveFullPathAsync(game);
            if (romPath is null) return;

            var entries = await Task.Run(() => StateImageLocator.GetRecent(corePath, romPath, MaxStateImages));
            if (entries.Count == 0) return;

            // 保存時の比率が記録されていない古いステートは、このゲームの既定の比率(DS/3DSは4:3)で代用する。
            double? fallbackAspect = game.ConsoleId is "nds" or "3ds"
                ? 4.0 / 3.0
                : ResolveLauncherFor(game)?.ResolveDefaultAspectRatio();

            var items = new List<StateImageItem>();
            foreach (var e in entries)
            {
                var image = await LoadBitmapAsync(e.ImagePath);
                if (image is null) continue;
                DateTime t = e.SavedAt.ToLocalTime();

                // 画像は映像のピクセル比のまま保存されているので、表示する比率へ伸縮して枠(260x146)に収める。
                double aspect = e.Aspect ?? fallbackAspect
                    ?? (image.PixelHeight > 0 ? (double)image.PixelWidth / image.PixelHeight : 4.0 / 3.0);
                if (aspect <= 0) aspect = 4.0 / 3.0;
                double w = 260, h = 146;
                if (aspect >= w / h) h = w / aspect; else w = h * aspect;

                items.Add(new StateImageItem
                {
                    ImageWidth = w,
                    ImageHeight = h,
                    Key = e.Key,
                    Image = image,
                    Label = e.Label,
                    DateText = t.Year == DateTime.Now.Year ? t.ToString("M/d HH:mm") : t.ToString("yyyy/M/d HH:mm"),
                });
            }

            // 待っている間に閉じた/別のゲームへ替わっていたら何もしない。
            if (items.Count == 0 || !IsOpen || !ReferenceEquals(CurrentGame, game)) return;

            foreach (var item in items) StateImages.Add(item);
            StateImageStrip.Visibility = Visibility.Visible;
            FitDescriptionLines(); // 画像の分だけ説明文の行数を減らす(余白に合わせる)
        }
        catch { /* 画像が出せないだけ。詳細画面自体は問題なく使える */ }
    }

    private void DescriptionHost_SizeChanged(object sender, SizeChangedEventArgs e) => FitDescriptionLines();

    /// <summary>
    /// 概要の行数を、概要の欄(見出しとコレクション/ステート欄にはさまれた余白)に収まる数(1〜6行)にする。
    /// 余白は欄の上の余白(28)を除き、少し余裕を見て数える。まだ大きさが決まっていないときは6行にしておく
    /// (決まった時点でSizeChangedが来て、改めて合わせる)。
    /// </summary>
    private void FitDescriptionLines()
    {
        double available = DescriptionHost.ActualHeight - 28 - 4;
        int lines = available <= 0 ? 6 : (int)Math.Floor(available / 42);
        DetailDescriptionText.MaxLines = Math.Clamp(lines, 1, 6);
    }

    /// <summary>同じパスへ上書き保存されるので、OSの画像キャッシュを避けてストリーム経由で読み込む。</summary>
    private static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> LoadBitmapAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch { return null; }
    }

    /// <summary>このゲームを起動するときに使うコア(libretro)のパス。外部ランチャー等ならnull。</summary>
    /// <summary>このゲームに再開用(中断)データがあるか。「ゲーム起動」から直接起動する時の判定用。</summary>
    public static async Task<bool> HasSuspendStateAsync(RomFile game)
    {
        try
        {
            string? corePath = ResolveCorePathFor(game);
            if (string.IsNullOrEmpty(corePath)) return false;
            string? romPath = game.IsBiosBoot ? string.Empty : await GameLibraryFacade.ResolveFullPathAsync(game);
            return romPath is not null && SuspendState.Exists(corePath, romPath);
        }
        catch { return false; }
    }

    private static LauncherInfo? ResolveLauncherFor(RomFile game)
    {
        ConsoleInfo? console = SampleData.GetConsoles().FirstOrDefault(c => c.Id == game.ConsoleId);
        LauncherInfo? launcher = string.IsNullOrEmpty(game.Launcher)
            ? null
            : SampleData.ResolveLauncher(game.ConsoleId, game.Launcher);
        return launcher ?? SampleData.ResolveLauncher(game.ConsoleId, console?.LauncherName ?? string.Empty);
    }

    private static string? ResolveCorePathFor(RomFile game)
    {
        ConsoleInfo? console = SampleData.GetConsoles().FirstOrDefault(c => c.Id == game.ConsoleId);
        LauncherInfo? launcher = string.IsNullOrEmpty(game.Launcher)
            ? null
            : SampleData.ResolveLauncher(game.ConsoleId, game.Launcher);
        launcher ??= SampleData.ResolveLauncher(game.ConsoleId, console?.LauncherName ?? string.Empty);
        if (launcher is null || launcher.IsExternal) return null;
        return SampleData.ResolveCorePath(launcher.CorePath ?? string.Empty);
    }

    private void RefreshTopFocusDisplay()
    {
        // チップ側の枠は、起動/ランチャー欄にフォーカスがある間は消す。
        for (int i = 0; i < DetailCollections.Count; i++)
        {
            DetailCollections[i].IsFocused = !TopFocused && i == _chipIndex;
        }
        for (int i = 0; i < StateImages.Count; i++)
        {
            StateImages[i].IsFocused = _statesFocused && i == _stateIndex;
        }
        UpdateLauncherDisplay();
        UpdateLaunchDisplay();
        UpdateButtonsDisplay();
    }

    private void UpdateLaunchDisplay()
    {
        LaunchBox.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)BrushConverter.Convert(_launchFocused, typeof(object), null!, "");
        ResumeBox.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)BrushConverter.Convert(_resumeFocused, typeof(object), null!, "");
        VariantBox.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)BrushConverter.Convert(_variantFocused, typeof(object), null!, "");
    }

    private void ResumeBox_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SetResumeFocus(true);
        ResumeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LaunchBox_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SetLaunchFocus(true);
        RaiseLaunch();
    }

    /// <summary>
    /// ランチャーの選択画面(全画面)を開き、選んだランチャーをこのゲームだけの指定として保存する。
    /// 「既定」を選ぶと指定を消す(コンソールの既定ランチャーに戻る)。
    /// </summary>
    private async Task ChangeLauncherAsync()
    {
        RomFile? game = CurrentGame;
        if (game is null || _launcherOptions.Count < 2 || ButtonChoiceOverlay.IsOpen) return;

        // 既定のランチャーが一覧(各ランチャー)に含まれているときは、「既定」の項目を別に出さない
        // (同じものが2つ並ぶため)。既定と同じものを選んだ場合は、指定なし(既定のまま)として保存する。
        bool defaultListed = !string.IsNullOrEmpty(_defaultLauncherName)
            && _launcherOptions.Any(o => o is not null && o.Name == _defaultLauncherName);

        var items = new List<GamepadPickerItem>();
        for (int i = 0; i < _launcherOptions.Count; i++)
        {
            LauncherInfo? option = _launcherOptions[i];
            if (option is null && defaultListed) continue;

            string label = option is null
                ? (string.IsNullOrEmpty(_defaultLauncherName) ? "既定" : $"既定({_defaultLauncherName})")
                : (defaultListed && option.Name == _defaultLauncherName ? $"{option.Name}(コンソール標準)" : option.Name);
            bool isCurrent = i == _launcherIndex
                || (_launcherIndex == 0 && defaultListed && option is not null && option.Name == _defaultLauncherName);
            items.Add(new GamepadPickerItem { Label = label, Tag = i, IsChecked = isCurrent });
        }

        GamepadPickerItem? picked = await ButtonChoiceOverlay.ShowAsync("ランチャー変更(このゲームだけ)", items);
        if (picked?.Tag is not int next || !ReferenceEquals(CurrentGame, game)) return;

        if (defaultListed && _launcherOptions[next]?.Name == _defaultLauncherName) next = 0;
        if (next == _launcherIndex) return;

        string value = _launcherOptions[next]?.Name ?? string.Empty;
        int previous = _launcherIndex;

        _launcherIndex = next;
        game.Launcher = value;
        UpdateLauncherDisplay();

        bool ok;
        try
        {
            ok = await GameLibraryFacade.UpdateLauncherAsync(game.No, value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameDetailOverlay] ランチャーの保存に失敗しました: {ex.Message}");
            ok = false;
        }

        // 保存に失敗したら表示と値を元へ戻す(まだ同じゲームを表示している場合のみ表示を更新する)。
        if (!ok)
        {
            game.Launcher = previous >= 0 && previous < _launcherOptions.Count
                ? _launcherOptions[previous]?.Name ?? string.Empty
                : string.Empty;
            if (ReferenceEquals(CurrentGame, game))
            {
                _launcherIndex = previous;
                UpdateLauncherDisplay();
            }
        }
    }

    // ------------------------------------------------------------------
    // 「ボタン割り当て」(このゲームだけの操作。RomFile.Customのcontrollerへ保存)
    // ------------------------------------------------------------------

    private void SetupButtonsDisplay(RomFile game) => UpdateButtonsDisplay();

    /// <summary>このゲームだけの割り当て(カスタム)があれば、画像の下に印を出す。</summary>
    private void UpdateButtonsDisplay()
    {
        int count = ControllerMappingService.CountGameConfig(CurrentGame?.Custom);
        CustomControllerBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>ゲーム別のコントローラ設定を保存する処理の連鎖(変更の順番どおりに1つずつ保存する)。</summary>
    private Task _controllerSaveChain = Task.CompletedTask;

    /// <summary>
    /// 「ボタン割り当て変更」。コントローラー設定画面(ControllerSettingsPage)をゲーム別モードで開く
    /// (コンソールの行以外は同じ画面)。変更はこのゲームのCustomだけに保存し、コンソールの設定は変えない。
    /// </summary>
    private void EditGameButtons() => _ = EditGameButtonsAsync();

    /// <summary>
    /// コントローラの形態が複数あるコンソール(Wii等)は、先に「このゲームで使うコントローラ」を選ばせ、
    /// 選んだ形態をこのゲームの設定にしたうえで、その形態のボタン割り当てを編集する。
    /// </summary>
    private async Task EditGameButtonsAsync()
    {
        RomFile? game = CurrentGame;
        if (game is null || ButtonChoiceOverlay.IsOpen || GameControllerPage.Visibility == Visibility.Visible) return;

        // このゲームが使うコアがプリセットを持つなら、そのコアの設定を編集する。持たなければコンソール単位(パススルー)。
        string scope = game.ConsoleId;
        ConsoleControllerProfile? profile = ControllerProfiles.FindProfile(game.ConsoleId, ResolveGameCoreFile(game));
        if (profile is { Forms.Count: > 1 })
        {
            string? formId = await PickControllerFormAsync(game, profile);
            if (formId is null || !ReferenceEquals(CurrentGame, game)) return;
            scope = ControllerProfiles.MakeScope(profile, formId);
        }
        else if (profile is { Forms.Count: 1 })
        {
            scope = ControllerProfiles.MakeScope(profile, profile.Forms[0].Id);
        }

        GameControllerPage.Visibility = Visibility.Visible;
        GameControllerPage.BeginGameMode(
            scope,
            game.DisplayTitle,
            game.Custom,
            mutation =>
            {
                _controllerSaveChain = _controllerSaveChain
                    .ContinueWith(_ => SaveControllerCustomAsync(game, mutation), TaskScheduler.Default)
                    .Unwrap();
            },
            () =>
            {
                game.Custom = GameControllerPage.GameCustom ?? string.Empty;
                GameControllerPage.Visibility = Visibility.Collapsed;
                if (ReferenceEquals(CurrentGame, game)) SetupButtonsDisplay(game);
            });
    }

    /// <summary>このゲームを起動するコアのdllファイル名(ゲーム別ランチャー → コンソールの既定ランチャーの順)。外部ランチャー等で無ければ空。</summary>
    private static string ResolveGameCoreFile(RomFile game)
    {
        LauncherInfo? launcher = string.IsNullOrEmpty(game.Launcher) ? null : SampleData.ResolveLauncher(game.ConsoleId, game.Launcher);
        if (launcher is null)
        {
            string defaultName = SampleData.GetConsoles().FirstOrDefault(c => c.Id == game.ConsoleId)?.LauncherName ?? string.Empty;
            launcher = SampleData.ResolveLauncher(game.ConsoleId, defaultName);
        }

        return launcher is null || launcher.IsExternal ? string.Empty : System.IO.Path.GetFileName(launcher.CorePath ?? string.Empty);
    }

    /// <summary>コアオプションの標準値(設定画面のコア詳細設定=cores/libretro.json。未設定ならDefault)から、標準の形態を引く。</summary>
    private static string GetBaseControllerForm(ConsoleControllerProfile profile)
    {
        LibretroCoreInfo? core = EmulatorLauncher.Services.LibretroCoreCatalogService.LoadCatalog().FirstOrDefault(
            c => string.Equals(c.CoreName, profile.Core, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(c.ConsoleId, profile.ConsoleId, StringComparison.OrdinalIgnoreCase));
        LibretroCoreOptionInfo? option = core?.Options.FirstOrDefault(o => string.Equals(o.Key, profile.OptionKey, StringComparison.Ordinal));
        string value = option is null ? string.Empty : (!string.IsNullOrEmpty(option.Value) ? option.Value! : option.Default);
        return string.IsNullOrEmpty(value) ? profile.Default : value;
    }

    /// <summary>
    /// 「このゲームで使うコントローラ」を選ばせ、選んだ形態をこのゲームのコアオプションとして保存する
    /// (標準と同じなら差分を消す)。選ばずに戻ったらnull。
    /// </summary>
    private async Task<string?> PickControllerFormAsync(RomFile game, ConsoleControllerProfile profile)
    {
        string baseValue = GetBaseControllerForm(profile);
        string? own = RomCustom.GetSectionString(RomCustom.Parse(game.Custom), RomCustom.OptionsKey, profile.OptionKey);
        string currentId = ControllerProfiles.FindForm(profile.ConsoleId, own ?? baseValue)?.Id ?? profile.Default;

        var items = profile.Forms
            .Select(f => new GamepadPickerItem { Label = f.Label, Tag = f.Id, IsChecked = f.Id == currentId })
            .ToList();
        GamepadPickerItem? picked = await ButtonChoiceOverlay.ShowAsync("このゲームで使うコントローラ", items);
        if (picked?.Tag is not string chosen) return null;

        if (chosen != currentId)
        {
            // コアオプションへ書く値(形態IDと違う場合がある。例: pad6 → "6 Buttons")。
            string optionKey = profile.OptionKey;
            string chosenValue = ControllerProfiles.FindForm(profile, chosen)?.ValueForOption ?? chosen;
            string normalizedBase = ControllerProfiles.FindForm(profile, baseValue)?.ValueForOption ?? baseValue;
            await SaveControllerCustomAsync(game, o => RomCustom.SetSectionDiff(o, RomCustom.OptionsKey, optionKey, chosenValue, normalizedBase));
        }

        return chosen;
    }

    /// <summary>このゲームのCustomを部分更新して保存し、画面が持つ値も更新する(失敗したら何もしない)。</summary>
    private static async Task SaveControllerCustomAsync(RomFile game, Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        try
        {
            string? saved = await GameLibraryFacade.UpdateCustomAsync(game.No, mutate);
            if (saved is not null) game.Custom = saved;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameDetailOverlay] 設定の保存に失敗しました: {ex.Message}");
        }
    }

    /// <summary>プレイ時間(分)を「3分」「1時間25分」の形にする。</summary>
    private static string FormatPlayTime(int minutes)
    {
        if (minutes < 60) return $"{minutes}分";
        int hours = minutes / 60;
        int rest = minutes % 60;
        return rest == 0 ? $"{hours}時間" : $"{hours}時間{rest}分";
    }

    /// <summary>画像の読み込みに失敗したときは、アクセントカラーの単色を出す。</summary>
    private void DetailThumbnailImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        DetailThumbnailFallback.Visibility = Visibility.Visible;
    }
}

/// <summary><see cref="GameDetailOverlay.MembershipChanged"/>の引数。</summary>
public sealed class CollectionMembershipChangedEventArgs : EventArgs
{
    public required RomFile Game { get; init; }
    public required string CollectionId { get; init; }

    /// <summary>変更後の所属状態(true=追加された、false=外された)。</summary>
    public required bool IsMember { get; init; }
}
