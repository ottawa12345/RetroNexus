using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.Interop;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using EmulatorLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;

namespace EmulatorLauncher.Views;

/// <summary>
/// ゲーム実行中の画面。emulator側(EmulationHost, SDL3 + libretro)の実映像を
/// このページに埋め込み、中断メニュー(セーブ/ロード/リセット/タイトルへ戻る)を
/// オーバーレイとして重ねる。
///
/// SDLイベントポンプの所有権について:
/// このページに入る(EmulationHost.Start()が成功する)と、以後はEmulationHostの
/// 専用スレッドがSDLイベントポンプの唯一の所有者になる。そのため、メニュー画面
/// (コンソール選択/ゲーム選択)のナビゲーションを担当する SdlGamepadService は
/// ここで明示的に停止し、このページを離れる(またはEmulationHostの起動に失敗する)
/// タイミングで再開する。中断メニュー自体のナビゲーションは、EmulationHostが
/// 一時停止中に読み取って転送してくれる PausedMenuButton* イベントで行う。
/// </summary>
public sealed partial class GamePlayPage : Page, IControllerPage
{
    private enum FocusArea { Menu, Slots, Settings, Disks }

    /// <summary>ゲーム実行中に一時停止して表示できるオーバーレイの種類。同時に1つだけ開ける。</summary>
    private enum PausedOverlay { None, Menu, Rewind }

    public System.Collections.Generic.List<PauseMenuItem> MenuItems { get; } = SampleData.GetPauseMenuItems();
    public System.Collections.Generic.List<SaveSlotInfo> SaveSlots { get; } = new();
    public System.Collections.Generic.List<RewindFrameInfo> RewindFrames { get; } = new();

    /// <summary>
    /// 中断メニュー「設定」を選んだときに右側の枠へ表示する項目(アスペクト比/画質など)。
    /// 表示中のコアがHWレンダリングかどうかで項目数が変わる(画質はSWレンダリング時のみ)ため、
    /// SaveSlots/RewindFramesと同じ要領で、開くたびにBuildSettingsItems()で作り直して
    /// ItemsControl.ItemsSourceへ明示的に付け替える。
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<SettingsMenuItem> SettingsItems { get; } = new();

    /// <summary>中断メニュー「ディスク入れ替え」の右側に出すディスク一覧(開くたびにBuildDiskItems()で作り直す)。</summary>
    public System.Collections.Generic.List<DiskMenuItem> DiskItems { get; } = new();

    /// <summary>
    /// x:Bindから参照する単純なプロパティ(このページはINotifyPropertyChangedを実装していないため、
    /// 値を変えたあとは明示的に Bindings.Update() を呼んでOneWayバインディングへ反映させる)。
    /// </summary>
    public bool HasSelectedRewindFrame { get; private set; }

    /// <summary>セーブ/ロードスロット・巻き戻しフィルムストリップのサムネイル箱の幅(論理px)。
    /// 高さは選択中の画面比率設定(GetDisplayAspectRatio)から逆算する。</summary>
    private const double ThumbnailBoxWidth = 200;

    private RomFile? _game;
    private ConsoleInfo _console = new();
    private EmulationHost? _emuHost;

    // 外部コマンド(libretroコアではない別プロセスのエミュレータ)で起動している場合のセッション。
    // ExternalAppSession参照。libretro起動(_emuHost)とは排他で、どちらか一方だけが非nullになる。
    private EmulatorLauncher.Services.ExternalAppSession? _externalSession;
    private LauncherInfo? _externalLauncher;
    private DateTime _externalStartedUtc;
    private Action<double>? _externalProgressHandler;

    /// <summary>外部コマンドを強制終了するために、ZL+ZRを押し続けてもらう時間。</summary>
    private static readonly TimeSpan ExternalKillHold = TimeSpan.FromMilliseconds(1500);

    /// <summary>この時間以内に異常終了コードで終了した場合は、起動失敗とみなしてエラーを表示したまま留まる。</summary>
    private static readonly TimeSpan ExternalQuickFailWindow = TimeSpan.FromSeconds(5);
    private CoreSettings _settings = new();

    /// <summary>コア毎の設定そのまま(ゲーム別の差分を重ねる前=標準値)。差分を保存するときの比較元。</summary>
    private CoreSettings _baseSettings = new();

    /// <summary>このゲームだけのコアオプション値(キー→値。標準値と違うものだけ)。RomFile.Custom "options"。</summary>
    private Dictionary<string, string> _gameOptions = new(StringComparer.Ordinal);

    /// <summary>操作説明バーを表示するか(全体設定。AppPreferences.ShowControlGuide)。</summary>
    private bool _showControlGuide = true;

    private Task _customSaveChain = Task.CompletedTask;

    /// <summary>RomFile.Customのゲーム別プレイ設定(画面比率・画質・連射・コアオプション)を_settings等へ重ねる。</summary>
    private void ApplyGameOverrides()
    {
        var custom = RomCustom.Parse(_game?.Custom);

        string? aspect = RomCustom.GetSectionString(custom, RomCustom.VideoKey, "aspect");
        if (aspect is not null && Enum.TryParse(aspect, out ScreenLayoutMode layout))
            _settings.ScreenLayout = layout;

        string? quality = RomCustom.GetSectionString(custom, RomCustom.VideoKey, "quality");
        if (quality is not null && Enum.TryParse(quality, out PictureQualityMode pq))
            _settings.PictureQuality = pq;

        _gameOptions = RomCustom.GetSectionStrings(custom, RomCustom.OptionsKey);
    }

    /// <summary>コアオプションの標準値(設定画面のコア詳細設定=cores/libretro.json。未設定ならDefault)。</summary>
    private static string GetBaseOptionValue(LibretroCoreOptionInfo option)
        => !string.IsNullOrEmpty(option.Value) ? option.Value! : option.Default;

    /// <summary>
    /// アプリ設定「地域」に対応するDolphinの本体言語値(0=日本語 1=英語 8=中国語(繁体))。
    /// 英語圏/欧州はゲーム側が多言語のため英語とする。
    /// </summary>
    private static string GetDolphinLanguageForRegion() => AppPreferencesStore.Load().Region switch
    {
        AppRegion.Japan => "0",
        AppRegion.HongKongTaiwanKorea => "8",
        _ => "1",
    };

    /// <summary>
    /// 言語を「地域」から決めるコアオプションの値。対象外のキーはnull。
    /// Dolphin=数値、melonDS=Japanese/English、Citra=Japanese/Traditional Chinese/English。
    /// </summary>
    private static string? GetRegionLanguage(string key)
    {
        AppRegion region = AppPreferencesStore.Load().Region;
        return key switch
        {
            "dolphin_language" => GetDolphinLanguageForRegion(),
            // PSP: 決定ボタン(○/×)と言語もアプリ設定の「地域」から決める。
            "ppsspp_button_preference" => region == AppRegion.Japan ? "Circle" : "Cross",
            "ppsspp_language" => region switch
            {
                AppRegion.Japan => "Japanese",
                AppRegion.HongKongTaiwanKorea => "Chinese Traditional",
                _ => "English",
            },
            "wswan_language" => region == AppRegion.Japan ? "japanese" : "english",
            "reicast_language" => region == AppRegion.Japan ? "Japanese" : "English",
            // Flycast(ドリームキャスト/NAOMI/Atomiswave)のリージョン。コアの標準はUSAなので、アプリの「地域」に合わせる。
            "reicast_region" => region switch
            {
                AppRegion.Japan => "Japan",
                AppRegion.Americas => "USA",
                AppRegion.Europe or AppRegion.Oceania => "Europe",
                _ => "Default",
            },
            "neocd_region" => region switch { AppRegion.Japan => "Japan", AppRegion.Americas => "USA", _ => "Europe" },
            "ngp_language" => region == AppRegion.Japan ? "japanese" : "english",
            "melonds_language" => region == AppRegion.Japan ? "Japanese" : "English",
            "citra_language" => region switch
            {
                AppRegion.Japan => "Japanese",
                AppRegion.HongKongTaiwanKorea => "Traditional Chinese",
                _ => "English",
            },
            _ => null,
        };
    }

    /// <summary>このゲームで実際に使うコアオプション値(ゲーム別の差分があればそれ、無ければ標準値)。</summary>
    private string GetEffectiveOptionValue(LibretroCoreOptionInfo option)
        => _gameOptions.TryGetValue(option.Key, out string? v) ? v : GetBaseOptionValue(option);

    /// <summary>
    /// RomFile.Customを部分更新する(メモリ上のゲームと、DBへ順番に書く)。
    /// 連続操作で書き込みの順序が入れ替わらないよう、保存は1本の列に並べる。
    /// </summary>
    private void SaveGameCustom(Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        RomFile? game = _game;
        if (game is null) return;

        var obj = RomCustom.Parse(game.Custom);
        mutate(obj);
        game.Custom = RomCustom.Serialize(obj);

        _customSaveChain = SaveCustomChainedAsync(_customSaveChain, game.No, mutate);
    }

    private static async Task SaveCustomChainedAsync(Task previous, int no, Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception) { }
        try
        {
            await GameLibraryFacade.UpdateCustomAsync(no, mutate).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GamePlayPage] ゲーム別設定の保存に失敗しました: {ex.Message}");
        }
    }
    private string _corePath = "";

    // ---- コア再起動(「再起動が必要」なコアオプションを変えたとき) ----
    // 設定枠を開いた時点の、再起動が必要な項目の値。閉じるときに比べて、違えば再起動する。
    private Dictionary<string, string>? _restartSnapshot;

    /// <summary>
    /// trueにしてからこのページへ移動すると、前回「タイトル選択にもどる」で保存した再開用ステート
    /// (SuspendState)から続きで始める。移動先のOnNavigatedToが読み取って、falseへ戻す。
    /// (詳細画面の「再開」ボタンから起動するときに、各ゲーム選択画面が立てる)
    /// </summary>
    public static bool ResumeOnNextLaunch { get; set; }

    /// <summary>
    /// 複数枚組(.m3u)を、何枚目のディスクから始めるか(0始まり。0=先頭のまま)。詳細画面の「ディスク」欄で選んで起動したときに立てる。
    /// OnNavigatedToが読み取って0へ戻す。再開(ステートから続き)のときは使わない。
    /// </summary>
    public static int InitialDiscIndex { get; set; }

    private int _initialDiscIndex;

    /// <summary>
    /// ResumeOnNextLaunchがtrueのとき、どのステートから再開するか("suspend"/"auto"/スロット番号)。
    /// nullは中断(suspend)。ステート画像を選んで再開する場合に、詳細画面が立てる。OnNavigatedToが読み取ってnullへ戻す。
    /// </summary>
    public static string? ResumeStateKey { get; set; }

    private string? _resumeStateKey;

    private bool _resumeFromSuspend;
    private string _romPathForRestart = "";
    private bool _restartInProgress;

    // ==================================================================
    // 起動中コア専用のコアオプション(cores/libretro.jsonのOptions)
    // ==================================================================
    // 以前はmupen64plus-nextの内部解像度/FXAAをそれぞれ専用のフィールド・メソッドとして
    // 個別にハードコードしていたが、SettingsPage(「コア毎の設定」タブ)と同じ考え方で、
    // 「cores/libretro.jsonのOptionsに項目を足すだけで、コアの種類に関係なく自動的に
    // このゲーム中の中断メニューにも出る」よう汎用化した。
    //
    // - Hide=falseの項目だけを中断メニュー「設定」パネルに表示し、ボタン送りで
    //   Choicesを循環させられる(BuildSettingsItems/CycleCoreOption参照)。
    // - Hide=trueの項目は画面には出さないが、値(Value、未設定ならDefault)は他の項目と
    //   全く同様にStartEmulationAsyncでコア起動前にSetInitialCoreVariableへ渡す
    //   (「設定変更は見せないが、コアには渡したい固定パラメータ」向け)。

    /// <summary>
    /// 起動中のコアに対応するcores/libretro.json側のカタログエントリ。該当エントリが
    /// 見つからない(コア名が一致しない、またはOptions自体が空)場合はnullのままで、
    /// その場合は中断メニューに何もコアオプションの項目を追加しない(BuildSettingsItems参照)。
    /// </summary>
    private LibretroCoreInfo? _currentCatalogCore;

    /// <summary>
    /// 起動中のコア(LauncherInfo.DefaultAspectRatio、Launchers.json由来)の表示ラベルをそのまま
    /// 保持したもの(例: "4:3"、"3:2"、"10:9")。ScreenLayoutModeText(LauncherDefaultモードの
    /// 表示名)、およびBuildScreenLayoutSequence(既に16:9と同じ値かどうかの判定)で使う。
    /// DefaultAspectRatioが"Auto"、または未解決(<see cref="_launcherDefaultAspectRatio"/>が
    /// null)の場合はnull。StartEmulationAsyncでコア解決時に設定する。
    /// </summary>
    private string? _launcherDefaultAspectRatioLabel;

    /// <summary>
    /// <see cref="_launcherDefaultAspectRatioLabel"/>を実際の比率(幅/高さ)へ解決した値。
    /// CoreSettings.ScreenLayout == ScreenLayoutMode.LauncherDefault のとき、
    /// GetDisplayAspectRatioがこの値を返す(nullの場合はAutoと同じくコアの申告値を使う。
    /// MAME/ニンテンドーDS等、DefaultAspectRatio="Auto"の機種はここが常にnullになる)。
    /// </summary>
    private double? _launcherDefaultAspectRatio;

    // デバッグ調査用: ApplyEmuHostLayoutが直前にログした値(スパム防止の重複排除用)。
    private double? _lastLoggedLayoutAspect;
    private int _lastLoggedLayoutWidthPx = int.MinValue;
    private int _lastLoggedLayoutHeightPx = int.MinValue;

    private PausedOverlay _pausedOverlay = PausedOverlay.None;
    private FocusArea _focus = FocusArea.Menu;
    private int _menuIndex;
    private int _slotIndex;
    private int _settingsIndex;
    private int _diskIndex;
    private bool _slotsAreSaveMode;
    private int _rewindIndex = -1;

    // ==================================================================
    // レイアウトの定期的な自己修正ポーリングについて
    // ==================================================================
    // 最大化⇔リストア(元に戻す)は、Win32のウィンドウメッセージ(WM_SIZE/
    // WM_WINDOWPOSCHANGED)を受け取った"その瞬間"に GetClientRect を呼んでも、
    // ごく稀に(実機での再現性は不安定)実際の最終ジオメトリと異なる値を
    // 読んでしまうことがあるとみられ、メッセージ駆動の即時更新だけでは
    // 取りこぼすケースがあった(SDL_Rendererへ切り替えた後も同じ症状が
    // 再現したため、原因は描画バックエンド側ではなく、このジオメトリ取得
    // タイミングそのものにあると考えられる)。
    // 「どの瞬間のGetClientRectなら必ず正しいか」を厳密に見極める代わりに、
    // 短い周期(150ms)で継続的に現在のジオメトリを読み直し、前回適用した
    // 値と異なっていれば再適用する、という単純な自己修正ポーリングを
    // 追加する。メッセージ駆動の即時更新はそのまま残し、これはあくまで
    // 「万一取りこぼした場合の保険」として働く。
    private readonly DispatcherQueueTimer _layoutPollTimer;

    // ==================================================================
    // アプリのアクティブ状態の定期的な自己修正ポーリングについて
    // ==================================================================
    // WM_ACTIVATEAPPだけに頼った検知(MainWindowのサブクラスで直接フック)は、
    // 実機検証の結果、他のアプリ(File Explorer等)へ切り替えても中断メニューへ
    // 移行しないことがあると分かった。原因は、WM_ACTIVATEAPPが「直前まで
    // 実際にアクティブだったウィンドウを作成したスレッド」へ送られる仕様に
    // ある。ユーザーが最後に操作していたのがゲーム画面(SDL描画用ウィンドウ、
    // EmulationHost専用の別スレッドで作成)だった場合、他のアプリへ切り替える
    // 際のWM_ACTIVATEAPPはそちらのスレッドへ送られ、MainWindowのスレッド
    // (私たちがサブクラスしているのはそちらだけ)にはそもそも届かない。
    // ゲーム画面へフォーカスがある状態から他アプリへ切り替えるのは非常に
    // よくある操作であるため、メッセージ駆動の検知だけでは実用上不十分。
    // → レイアウトの自己修正ポーリングと同じ仕組みで、短い周期(150ms)ごとに
    // GetForegroundWindow/GetWindowThreadProcessIdで「今、前面にあるのは
    // 自分自身のプロセスのウィンドウか」を直接確認し、他プロセスへ切り替わって
    // いれば中断メニューを表示する。WM_ACTIVATEAPPによる即時検知はそのまま
    // 残し(うまく届く場合は素早く反応できるため)、これはあくまで
    // 「取りこぼした場合の保険」として働く。
    private bool _lastKnownAppActive = true;

    public GamePlayPage()
    {
        this.InitializeComponent();
        SystemSettings.InGame = true; // ゲーム中はスリープ/再起動/シャットダウンを選べない

        _layoutPollTimer = DispatcherQueue.CreateTimer();
        _layoutPollTimer.Interval = TimeSpan.FromMilliseconds(150);
        _layoutPollTimer.IsRepeating = true;
        _layoutPollTimer.Tick += (_, _) =>
        {
            ApplyEmuHostLayout();
            PollAppActiveState();
            ReassertNativeWindowHiddenIfPaused();
            SamplePlayTime();
        };

        Unloaded += (_, _) =>
        {
            _layoutPollTimer.Stop();
            TeardownEmulation();
        };
    }

    /// <summary>
    /// レイアウトの再計算が必要になったタイミング(起動時・ウィンドウ移動/リサイズ時・
    /// コア準備完了時)で呼ぶ。
    ///
    /// ApplyEmuHostLayout() は Win32 の GetClientRect/ClientToScreen から直接
    /// 「今の」クライアント領域を取得するため、WinUI3側のXAMLレイアウト確定を
    /// 待つ必要がない。また、EmulationHost側でネイティブウィンドウのリサイズを
    /// SDL自身のAPI(SDL_SetWindowPosition/SDL_SetWindowSize)で行うように
    /// なったため、ウィンドウの最大化⇔リストア時にDWM(Desktop Window Manager)が
    /// 再生する伸縮アニメーションの影響も受けなくなった。そのため、ここでの
    /// 再計算はメッセージ駆動での即時実行に加え、GamePlayPage側の
    /// _layoutPollTimer による定期的な自己修正ポーリングだけで十分。
    /// </summary>
    private void ScheduleLayoutRecalc()
    {
        ApplyEmuHostLayout();
        if (_pausedOverlay == PausedOverlay.Rewind)
            ApplyRewindPreviewAspect();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _playStarted = false;
        _playRecorded = false;
        _playExternal = false;
        _playActiveSeconds = 0;
        _playLastSampleUtc = null;
        _guideIntroPlayed = false;

        _resumeFromSuspend = ResumeOnNextLaunch;
        ResumeOnNextLaunch = false;
        _resumeStateKey = ResumeStateKey;
        ResumeStateKey = null;
        _initialDiscIndex = InitialDiscIndex;
        InitialDiscIndex = 0;
        Debug.WriteLine($"[GamePlayPage] OnNavigatedTo: 再開で起動={_resumeFromSuspend}");

        if (e.Parameter is RomFile game)
        {
            _game = game;
            _console = SampleData.GetConsoles().FirstOrDefault(c => c.Id == game.ConsoleId) ?? new ConsoleInfo();
        }

        if (AppServices.MainWindow is { } window)
        {
            window.WindowBoundsChanged += OnWindowBoundsChanged;
            window.MinimizedStateChanged += OnMainWindowMinimizedStateChanged;
            window.AppActiveStateChanged += OnMainWindowAppActiveStateChanged;
        }

        // ゲーム開始の効果音(ゲーム選択画面から起動したとき)。
        EmulatorLauncher.Services.SoundEffects.Play("start");

        Task launchAnimation = BeginLaunchCoverAnimation();

        SetMenuIndex(0);
        _ = StartEmulationAfterLaunchAnimationAsync(launchAnimation);
    }

    /// <summary>
    /// 起動時のカバー画像の拡大アニメーションが終わるまで待ってからエミュレーションを始める。
    /// (ゲーム画面は別ウィンドウで最前面に出るため、待たないとアニメーションが見えないうちに
    ///  ゲーム画面で覆われてしまう。アニメーションが無い/失敗した場合は待たない)
    /// </summary>
    private async Task StartEmulationAfterLaunchAnimationAsync(Task launchAnimation)
    {
        try { await launchAnimation; } catch { }

        // 待っている間に画面を離れていた(Bで戻った等)場合は起動しない。
        if (!ReferenceEquals(Frame?.Content, this)) return;

        await StartEmulationAsync();
    }

    // ------------------------------------------------------------------
    // 起動/終了時のカバー画像の連結アニメーション
    // (ゲーム選択画面のカード ⇔ このページ全体。選択画面側で同じキーを使う)
    // ------------------------------------------------------------------
    public const string LaunchCoverKey = "GameLaunchCover";
    public const string ReturnCoverKey = "GameReturnCover";

    private static readonly EmulatorLauncher.Converters.IconPathToImageSourceConverter _coverConverter = new();

    private void EnsureLaunchCoverSource()
    {
        if (LaunchCoverImage.Source is not null || _game is null || string.IsNullOrEmpty(_game.IconPath)) return;
        LaunchCoverImage.Source = (Microsoft.UI.Xaml.Media.ImageSource?)_coverConverter.Convert(_game.IconPath, typeof(object), null!, "");
    }

    /// <summary>選択画面のカードから、このページへ拡大しながら着地する。画像が無い等の場合は何もしない。</summary>
    /// <returns>アニメーションが終わる(または何も起きない/失敗した)時点で完了するTask。</returns>
    private Task BeginLaunchCoverAnimation()
    {
        var done = new TaskCompletionSource();
        try
        {
            ConnectedAnimation? animation = ConnectedAnimationService.GetForCurrentView().GetAnimation(LaunchCoverKey);
            if (animation is null || _game is null || string.IsNullOrEmpty(_game.IconPath)) return Task.CompletedTask;

            // 何があっても一定時間でゲーム起動へ進む(画像の読み込みが終わらない等の安全弁)。
            _ = Task.Delay(1500).ContinueWith(_ => done.TrySetResult());

            void Start()
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        LaunchCoverImage.UpdateLayout();
                        animation.Completed += (_, _) => done.TrySetResult();
                        if (!animation.TryStart(LaunchCoverImage)) done.TrySetResult();
                    }
                    catch { done.TrySetResult(); }
                });
            }

            RoutedEventHandler? opened = null;
            ExceptionRoutedEventHandler? failed = null;
            opened = (_, _) => { LaunchCoverImage.ImageOpened -= opened; LaunchCoverImage.ImageFailed -= failed; Start(); };
            failed = (_, _) =>
            {
                LaunchCoverImage.ImageOpened -= opened; LaunchCoverImage.ImageFailed -= failed;
                LaunchCoverImage.Visibility = Visibility.Collapsed;
                done.TrySetResult();
            };
            LaunchCoverImage.ImageOpened += opened;
            LaunchCoverImage.ImageFailed += failed;

            EnsureLaunchCoverSource();
            LaunchCoverImage.Visibility = Visibility.Visible;
        }
        catch { done.TrySetResult(); }
        return done.Task;
    }

    /// <summary>
    /// ゲーム選択画面へ戻る。以前は、カバー画像がカードへ縮んで戻る連結アニメーションを仕込んでいたが、
    /// 戻り先でカードが見つかるまで待つあいだ、パッケージ画像が画面に残って「なかなか戻らない」
    /// ように見えたため、戻るときはカバー画像を出さず、そのまま戻る(起動時の拡大アニメーションは従来どおり)。
    /// 名前は呼び出し元との互換のため残している。
    /// </summary>
    private void GoBackWithCoverAnimation()
    {
        LaunchCoverImage.Visibility = Visibility.Collapsed;
        Frame.GoBack();
    }

    // ==================================================================
    // EmulationHost 起動/停止
    // ==================================================================

    /// <summary>
    /// ROMのフルパスをRomFileRepository経由(RomFile.ParentId→親RomFolder.Pathの解決)で
    /// 非同期に取得する必要があるため、OnNavigatedTo(戻り値void)からは
    /// 「発火して待たない」形で呼んでいる。
    /// </summary>
    private async Task StartEmulationAsync()
    {
        System.Diagnostics.Debug.WriteLine("[GamePlayPage] StartEmulationAsync: 開始");
        if (_game is null || AppServices.MainWindow is null)
        {
            //StatusText.Text = "起動できません(ゲーム情報が見つかりません)";
            return;
        }

        // ゲーム別のランチャー指定(RomFile.Launcher)があればそれを使う。空、または指定した
        // ランチャーが見つからない(削除された等)ときは、コンソールの既定ランチャーで起動する。
        var launcher = string.IsNullOrEmpty(_game.Launcher)
            ? null
            : SampleData.ResolveLauncher(_console.Id, _game.Launcher);
        launcher ??= SampleData.ResolveLauncher(_console.Id, _console.LauncherName);
        string corePath = SampleData.ResolveCorePath(launcher?.CorePath ?? string.Empty);

        // Launchers.json側の既定アスペクト比(実際のゲーム画面比率)。DefaultAspectRatioが
        // "Auto"(MAME/ニンテンドーDS等)、または解析できない値の場合はnullになる
        // (GetDisplayAspectRatio/BuildScreenLayoutSequenceは、その場合Autoと同じ扱いにする)。
        EmulatorLauncher.Services.AppLog.Info("Play",
            $"起動: コンソール={_console.Id} / ゲーム={_game.FileName} / ランチャー={launcher?.Name ?? "(未設定)"} / コア={(string.IsNullOrEmpty(corePath) ? "(未解決)" : corePath)}"
            + (launcher is null ? " ※ランチャーが見つかりません" : string.Empty)
            + (!string.IsNullOrEmpty(corePath) && !System.IO.File.Exists(corePath) ? " ※コアのファイルがありません" : string.Empty));
        _launcherDefaultAspectRatio = launcher?.ResolveDefaultAspectRatio();
        _launcherDefaultAspectRatioLabel = _launcherDefaultAspectRatio.HasValue
            ? launcher!.DefaultAspectRatio.Trim()
            : null;

        // 本体(BIOS)起動: ROMを持たず、空のパスでコアのretro_load_game(NULL)を呼ぶ。
        bool biosBoot = _game.IsBiosBoot;
        if (biosBoot && (launcher is null || launcher.IsExternal || !launcher.BiosBoot))
        {
            HandleFatalError("このコンソールは本体(BIOS)単体の起動に対応していません。");
            return;
        }

        string? romPath = biosBoot ? string.Empty : await GameLibraryFacade.ResolveFullPathAsync(_game);
        System.Diagnostics.Debug.WriteLine($"[GamePlayPage] StartEmulationAsync: ROMパス解決 => {romPath ?? "(null)"}");
        if (romPath is null)
        {
            HandleFatalError($"ROMの場所を特定できませんでした。\n{_game.FileName}\n\n対処: このゲームが入っていたゲームフォルダが、登録から削除されている可能性があります。ゲームフォルダを登録し直してください。");
            return;
        }

        if (launcher is { IsExternal: true })
        {
            StartExternalApp(launcher, romPath);
            return;
        }

        // 起動前の確認。原因が分かるものは、コアの生のエラーを待たずに対処つきで知らせる。
        if (launcher is null)
        {
            HandleFatalError("このゲームの起動方法(ランチャー)が設定されていません。\n\n対処: 設定の「ランチャー管理」で、このコンソールのランチャーを設定してください。");
            return;
        }
        if (string.IsNullOrEmpty(corePath) || !System.IO.File.Exists(corePath))
        {
            HandleFatalError($"このゲームを動かすコアがインストールされていません。\n({launcher.Name})\n\n対処: 設定の「コア管理」でコアをインストールしてください。");
            return;
        }
        if (!biosBoot && !System.IO.File.Exists(romPath))
        {
            HandleFatalError($"ROMファイルが見つかりません。\n{romPath}\n\n対処: 外付けドライブやSDカードが接続されているか確認してください。ファイルを移動・削除した場合は、設定の「ゲームフォルダの更新」で一覧を整理できます。");
            return;
        }

        // 複数枚組(.m3u)は、書かれているディスクが全部そろっているときだけ起動する(足りないまま起動すると、コアが原因を示さずに失敗するため)。
        if (!biosBoot && _game.IsDiscSet)
        {
            var missingDiscs = GameLibraryFacade.FindMissingDiscs(romPath);
            if (missingDiscs.Count > 0)
            {
                HandleFatalError("複数枚組のゲームに、見つからないディスクがあります。\n"
                    + string.Join("\n", missingDiscs.Take(5).Select(missingPath => System.IO.Path.GetFileName(missingPath)))
                    + (missingDiscs.Count > 5 ? $"\n…ほか{missingDiscs.Count - 5}枚" : string.Empty)
                    + "\n\n対処: ディスクのファイルがそろっているか確認してください。.m3uの中身を直すか、「ゲーム情報を編集」でまとめを解除して作り直せます。");
                return;
            }
        }

        _corePath = corePath; // 設定保存(CycleScreenLayout)用に保持

        // cores/libretro.json側に永続化済みのコアオプション(無ければDefault)を読み出して
        // おく。実際にコアへ渡すのはStart()より前(下記のSetInitialCoreVariable呼び出し箇所)。
        ResolveCurrentCoreOptions(corePath);

        // これ以降、EmulationHostの専用スレッドがSDLイベントポンプの唯一の所有者になる。
        // 同時に2箇所からSDL_PumpEvents/SDL_PollEventを呼ばないよう、メニュー用の
        // SdlGamepadServiceは停止しておく。
        AppServices.Gamepad?.Stop();

        // コア毎の設定(標準値)を読み、ゲーム別の差分(RomFile.Custom)を上に重ねる。
        _baseSettings = CoreSettingsStore.Load(corePath);
        _settings = CoreSettingsStore.Load(corePath);
        ApplyGameOverrides();

        // 操作説明表示は全体設定(アプリ設定)。コア詳細設定で、このコアだけ上書きしていればそれを使う。
        _showControlGuide = _settings.ControlGuide switch
        {
            CoreSettings.ControlGuideOn => true,
            CoreSettings.ControlGuideOff => false,
            _ => AppPreferencesStore.Load().ShowControlGuide,
        };

        // 「操作説明表示」トグルの現在値を、下部ガイドバー(PlayHintBar)へ即座に反映する。
        // EmulationHostの起動有無に関係なく常設のバーなので、ここで先に適用しておく。
        ApplyShowControlGuide();

        _romPathForRestart = romPath;
        if (StartHostCore(corePath, romPath, resumeFromRestartState: false, resumeFromSuspend: _resumeFromSuspend))
        {
            BeginPlayTracking(external: false);
        }
    }

    /// <summary>
    /// EmulationHostを作ってコアを起動する(最初の起動と、オプション変更に伴う再起動の両方で使う)。
    /// 成功したらtrue。失敗時はHandleFatalErrorを呼んでfalseを返す。
    /// </summary>
    private bool StartHostCore(string corePath, string romPath, bool resumeFromRestartState, bool resumeFromSuspend = false)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[GamePlayPage] StartEmulationAsync: EmulationHost生成");
            _emuHost = new EmulationHost(AppServices.MainWindow!.Hwnd, corePath, romPath);
            _emuHost.StatusChanged += OnEmuStatusChanged;
            _emuHost.CoreReady += OnEmuCoreReady;
            _emuHost.GeometryChanged += OnEmuGeometryChanged;
            _emuHost.MenuToggleRequested += OnEmuMenuToggleRequested;
            _emuHost.SystemPanelRequested += OnEmuSystemPanelRequested;
            _emuHost.RewindToggleRequested += OnEmuRewindToggleRequested;
            _emuHost.RewindBecameUnavailable += OnEmuRewindBecameUnavailable;
            _emuHost.FastForwardMultiplierChanged += OnEmuFastForwardMultiplierChanged;
            _emuHost.PausedMenuButtonPressed += OnPausedMenuButtonPressed;
            _emuHost.PausedMenuButtonLongPressed += OnPausedMenuButtonLongPressed;
            _emuHost.PausedMenuButtonRepeated += OnPausedMenuButtonPressed;
            _emuHost.AutoSaveEnabled = _settings.AutoSaveEnabled;
            // 複数枚組を途中のディスクから始める指定(再開やコア再起動のときは、ステートが持つディスクのままにする)。
            _emuHost.InitialDiskIndex = resumeFromSuspend || resumeFromRestartState ? 0 : _initialDiscIndex;
            _emuHost.RewindEnabled = _settings.RewindEnabled;
            // ステートの保存時に、いま画面に出している比率を記録する(詳細画面のステート画像を同じ比率で出すため)。
            _emuHost.DisplayAspectProvider = GetStateImageAspect;
            _emuHost.PictureQuality = _settings.PictureQuality;
            // コントローラー設定画面で、このゲームのコンソールに設定したボタン割り当てと
            // ホットキー(中断メニュー/巻き戻し/早送り)を適用する。
            // 優先順位: 共通の下地 < コンソール毎の設定 < このゲームだけの設定。
            // コアへ渡すボタンの絞り込み(旧Consoles.jsonのbuttons)とコア特有の差し替え(旧input_remap)は廃止した。
            // このゲームだけの設定(RomFile.Custom)があれば、コンソールの設定の上に重ねる。
            // コントローラの形態(Wiiのヌンチャク等)を持つコンソールは、その形態の設定を
            // 「その機種で共通」の設定の上に重ねる(ApplyControllerMapping)。
            ApplyControllerMapping();
            _emuHost.ControllerPortDevice = ResolveControllerPortDevice();
            ApplyLightgunSettings();

            // 起動中コアのcores/libretro.json側のOptions(Hideの有無に関係なく全項目)を、
            // 必ずStart()より前に予約しておく。Start()後(retro_load_game完了後)に
            // RequestSetCoreVariableで渡しても、コアが最初にGET_VARIABLEを読んだ時点
            // (retro_load_game内。retro_get_system_av_infoのgeometry.max_width/max_height、
            // ひいてはHWレンダリング用FBOのサイズ計算に影響する場合がある)には間に合わない
            // (SetInitialCoreVariableのドキュメントコメント参照)。
            if (_currentCatalogCore is not null)
            {
                foreach (LibretroCoreOptionInfo option in _currentCatalogCore.Options)
                {
                    string effectiveValue = GetEffectiveOptionValue(option);
                    // 本体/ゲーム内の言語は、アプリ設定の「地域」から決める(コア設定には出さない)。
                    if (GetRegionLanguage(option.Key) is { } regionLanguage) effectiveValue = regionLanguage;
                    if (!string.IsNullOrEmpty(effectiveValue))
                    {
                        _emuHost.SetInitialCoreVariable(option.Key, effectiveValue);
                    }
                }
            }

            // コア再起動からの復帰なら、起動後に再起動用ステートを読み込んで続きから再開する。
            if (resumeFromRestartState) _emuHost.RequestLoadRestartState();
            // 詳細画面の「再開」から起動したときは、前回の終了時に保存した再開用ステートを読み込む。
            else if (resumeFromSuspend && _resumeStateKey is { } stateKey && stateKey != SuspendState.FolderName)
            {
                // 詳細画面でステート画像(手動スロット/オートセーブ)を選んで起動したとき。
                string stateFolder = SuspendState.GetStateFolder(corePath, romPath, stateKey);
                if (System.IO.File.Exists(System.IO.Path.Combine(stateFolder, "state.dat")))
                {
                    Debug.WriteLine($"[GamePlayPage] ステートの読み込みを要求: {stateFolder}");
                    _emuHost.RequestLoadStateOnStart(stateKey);
                }
                else
                {
                    Debug.WriteLine($"[GamePlayPage] 選ばれたステートが見つかりません: {stateFolder}");
                }
            }
            else if (resumeFromSuspend && SuspendState.Exists(corePath, romPath))
            {
                Debug.WriteLine($"[GamePlayPage] 再開用ステートの読み込みを要求: {SuspendState.GetFolder(corePath, romPath)}");
                _emuHost.RequestLoadSuspendState();
            }
            else if (resumeFromSuspend)
            {
                Debug.WriteLine($"[GamePlayPage] 再開で起動したが、再開用ステートが見つかりません: {SuspendState.GetFolder(corePath, romPath)}");
            }

            _emuHost.Start();
            System.Diagnostics.Debug.WriteLine("[GamePlayPage] StartEmulationAsync: EmulationHost.Start()呼び出し完了");
            ScheduleLayoutRecalc();
            _layoutPollTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            HandleFatalError($"起動失敗: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// いま起動しているコアと、選ばれているコントローラの形態に合わせた設定の置き場所(例 "wii@dolphin/nunchuk")。
    /// そのコアがプリセットを持たない(パススルー)ときは、コンソールIDそのまま。
    /// </summary>
    private string ResolveControllerScope(string consoleId)
    {
        ConsoleControllerProfile? profile = ControllerProfiles.FindProfile(consoleId, _currentCatalogCore?.CoreName);
        if (profile is null) return consoleId;

        string? formId = null;
        LibretroCoreOptionInfo? option = _currentCatalogCore?.Options.FirstOrDefault(
            o => string.Equals(o.Key, profile.OptionKey, StringComparison.Ordinal));
        if (option is not null) formId = GetEffectiveOptionValue(option);
        if (string.IsNullOrEmpty(formId)) formId = profile.Default;

        return ControllerProfiles.ResolveScope(consoleId, profile.Core, formId);
    }

    /// <summary>
    /// 選ばれているコントローラの形態のデバイス値(Wii以外。擬似オプション"retronexus_controller"で選ぶ機種のみ)。
    /// それ以外はnull(通常のジョイパッド。Wiiはホスト側の別経路で決まる)。
    /// </summary>
    private uint? ResolveControllerPortDevice()
    {
        ConsoleControllerProfile? profile = ControllerProfiles.FindProfile(_console?.Id ?? string.Empty, _currentCatalogCore?.CoreName);
        if (profile is null || !string.Equals(profile.OptionKey, EmulationHost.ControllerFormKey, StringComparison.Ordinal)) return null;

        LibretroCoreOptionInfo? option = _currentCatalogCore?.Options.FirstOrDefault(
            o => string.Equals(o.Key, profile.OptionKey, StringComparison.Ordinal));
        string? formId = option is null ? null : GetEffectiveOptionValue(option);
        if (string.IsNullOrEmpty(formId)) formId = profile.Default;

        uint device = ControllerProfiles.FindForm(profile, formId)?.Device ?? 0;
        return device == 0 ? null : device;
    }

    /// <summary>
    /// ライトガンの照準方法を、選ばれている形態に合わせてEmulationHostへ渡す。
    /// Wii=ジャイロ(従来どおり)。それ以外は形態ID: lightgun=右スティックで動かす / lightgun_abs=右スティックの位置へ直接 /
    /// lightgun_gyro=ジャイロ(どれもマウス/タッチを併用できる)。
    /// </summary>
    private void ApplyLightgunSettings()
    {
        if (_emuHost is null) return;

        _emuHost.LightgunWii = string.Equals(_console?.Id, "wii", StringComparison.OrdinalIgnoreCase);

        // DSはマウスでタッチ操作し、コアが位置を描画しないので、カーソルを隠さない。右スティック+R3でもタッチできる。
        // 3DSはコアがタッチ位置の照準を描画するので、マウスカーソルは隠す。
        bool isDs = string.Equals(_console?.Id, "nds", StringComparison.OrdinalIgnoreCase);
        _emuHost.KeepCursorVisible = isDs;
        _emuHost.StickCursorEnabled = isDs;

        string formId = string.Empty;
        ConsoleControllerProfile? profile = ControllerProfiles.FindProfile(_console?.Id ?? string.Empty, _currentCatalogCore?.CoreName);
        if (profile is not null && string.Equals(profile.OptionKey, EmulationHost.ControllerFormKey, StringComparison.Ordinal))
        {
            LibretroCoreOptionInfo? option = _currentCatalogCore?.Options.FirstOrDefault(
                o => string.Equals(o.Key, profile.OptionKey, StringComparison.Ordinal));
            formId = (option is null ? null : GetEffectiveOptionValue(option)) ?? string.Empty;
            if (formId.Length == 0) formId = profile.Default ?? string.Empty;
        }

        _emuHost.LightgunAimMode = formId switch
        {
            "lightgun_gyro" => 2,
            _ => 0,
        };
    }

    /// <summary>ボタン割り当て・ホットキー・連射を、いまの形態の設定で組み立ててEmulationHostへ渡す。</summary>
    private void ApplyControllerMapping()
    {
        if (_emuHost is null) return;

        string consoleId = _console?.Id ?? string.Empty;
        string scope = ResolveControllerScope(consoleId);
        _emuHost.ButtonMapping = ControllerMappingService.ResolveMapping(scope, _game?.Custom);
        System.Diagnostics.Debug.WriteLine($"[Controller] preset={ControllerProfiles.ResolvePresetId(scope)} scope={scope}");
        _emuHost.Hotkeys = ControllerMappingService.ResolveHotkeys(scope, _game?.Custom);
        ApplyMenuHotkeyHint(_emuHost.Hotkeys);
        _emuHost.TurboMapping = ControllerMappingService.ResolveTurbo(scope, _game?.Custom);
        // キーボードをコアへ渡すか(controllerProfiles.jsonの"keyboard"に載っているコンソールだけ)。
        _emuHost.KeyboardPassthrough = ControllerMappingService.IsKeyboardEnabled(scope, _game?.Custom);
        _emuHost.KeyboardPort = ControllerProfiles.FindKeyboardOption(scope)?.Port;
        // ポート1(2P側)の装置(controllerProfiles.jsonの"port1"に載っているコンソールだけ)。
        _emuHost.Port1Device = ControllerProfiles.FindPort1Option(scope) is { } port1Option
            ? port1Option.FindDevice(ControllerMappingService.GetPort1DeviceId(scope, _game?.Custom))?.Device
            : null;
    }

    /// <summary>下部ガイドバーの「中断メニュー」表示を、コントローラー設定で割り当てた組み合わせに合わせる。</summary>
    private void ApplyMenuHotkeyHint(ControllerMappingService.HotkeyBindings hotkeys)
    {
        _hintHotkeys = hotkeys;

        if (hotkeys.Menu.Length == 0)
        {
            MenuHint.Key = "-";
            MenuHint.Label = "中断メニュー(割り当てなし)";
            UpdateExtraHotkeyHints();
            return;
        }

        MenuHint.Key = string.Join("+", hotkeys.Menu.Select(HintButtonLabel));

        bool sameAsRewind = hotkeys.Rewind.Length == hotkeys.Menu.Length
                            && hotkeys.Menu.All(b => hotkeys.Rewind.Contains(b));
        _menuSameAsRewind = sameAsRewind;
        UpdateMenuHintLabel();
    }

    /// <summary>ホットキーのヒントに出すボタン名(1Pのコントローラーの種類に合わせる)。</summary>
    private static string HintButtonLabel(GamepadButton button)
        => ControllerGlyphs.GetButtonLabel(button, ControllerGlyphs.GetPrimaryPadStyle());

    /// <summary>巻き戻しが使えるか。HWレンダリングのコアと、ステートの保存が遅すぎて止めたゲームでは使えない。</summary>
    private bool IsRewindAvailable => _emuHost is null || (!_emuHost.IsHwRendered && !_emuHost.IsRewindUnavailable);

    private void OnEmuRewindBecameUnavailable(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateMenuHintLabel);

    /// <summary>中断メニューと巻き戻しが同じ組み合わせか(長押しで巻き戻しになるか)。</summary>
    private bool _menuSameAsRewind;

    // 下部ガイドバーに出す、中断メニュー以外のホットキー(早送り・巻き戻し)。割り当てがあるものだけ出す。
    private ControllerMappingService.HotkeyBindings? _hintHotkeys;
    private readonly EmulatorLauncher.Controls.HintDefinition _fastForwardHint = new() { Label = "早送り" };
    private readonly EmulatorLauncher.Controls.HintDefinition _rewindHint = new() { Label = "巻き戻し" };

    /// <summary>
    /// 下部ガイドバーの「早送り」「巻き戻し」を、いまのホットキーの割り当てに合わせて出し入れする
    /// (並びは 中断メニュー → 早送り → 巻き戻し → 決定)。その機能が無い/割り当てなしなら出さない。
    /// 巻き戻しは、中断メニューと同じ組み合わせ(長押し)のときは中断メニュー側の表示に含めるので出さず、
    /// HWレンダリングのコアでは使えないので出さない。
    /// </summary>
    private void UpdateExtraHotkeyHints()
    {
        PlayHintBar.Hints.Remove(_fastForwardHint);
        PlayHintBar.Hints.Remove(_rewindHint);
        if (_hintHotkeys is not { } hotkeys) return;

        int index = PlayHintBar.Hints.IndexOf(MenuHint) + 1;
        if (hotkeys.FastForward.Length > 0)
        {
            _fastForwardHint.Key = string.Join("+", hotkeys.FastForward.Select(HintButtonLabel));
            // 中断メニューと同じ組み合わせ(巻き戻しとは別)のときは、タップ=中断メニュー、長押し=早送りになる。
            bool fastForwardIsLongPress = !_menuSameAsRewind
                && hotkeys.Menu.Length > 0
                && hotkeys.Menu.Length == hotkeys.FastForward.Length
                && hotkeys.Menu.All(b => hotkeys.FastForward.Contains(b));
            _fastForwardHint.Label = fastForwardIsLongPress ? "早送り(長押し)" : "早送り";
            PlayHintBar.Hints.Insert(index++, _fastForwardHint);
        }

        bool rewindAvailable = IsRewindAvailable;
        if (hotkeys.Rewind.Length > 0 && rewindAvailable && !_menuSameAsRewind)
        {
            _rewindHint.Key = string.Join("+", hotkeys.Rewind.Select(HintButtonLabel));
            PlayHintBar.Hints.Insert(index, _rewindHint);
        }
    }

    /// <summary>
    /// 「中断メニュー」のヒント文言を更新する。「(長押しで巻き戻し)」は、巻き戻しが実際に使えるとき
    /// (SWレンダリングのコア)だけ付ける。HWレンダリングのコアは巻き戻しが無効なので付けない。
    /// コアのロード完了前(IsHwRenderedが未確定)は付けた状態で出し、ロード完了時に再判定する。
    /// </summary>
    private void UpdateMenuHintLabel()
    {
        if (MenuHint.Key == "-") return; // 割り当てなしの表示のときは触らない
        bool rewindAvailable = IsRewindAvailable;
        MenuHint.Label = _menuSameAsRewind && rewindAvailable ? "中断メニュー(長押しで巻き戻し)" : "中断メニュー";
        UpdateExtraHotkeyHints();
    }

    // ==================================================================
    // プレイ時間・最終プレイ日時の記録(RomFile.PlayTimeMinutes / LastPlayed)
    // ==================================================================
    // libretroのゲームは、エミュレーションが動いている時間だけを数える(中断メニュー表示中などの
    // 一時停止中・アプリが非アクティブで止まっている間は数えない)。外部アプリは起動から終了までの
    // 実時間を数える。ゲームを離れる(または外部アプリが終了する)ときに、DBへ1回だけ記録する。
    // プレイ時間は分単位(RomFile.PlayTimeMinutes)なので、1回のプレイ時間を四捨五入した分数を加算する
    // (30秒未満のプレイは0分加算だが、最終プレイ日時は更新する)。

    /// <summary>これより短い起動(起動直後の失敗・誤操作)はプレイとして記録しない。</summary>
    private const double MinRecordedPlaySeconds = 5;

    private bool _playStarted;
    private bool _playRecorded;
    private bool _playExternal;
    private double _playActiveSeconds;
    private DateTime? _playLastSampleUtc;

    private void BeginPlayTracking(bool external)
    {
        _playStarted = true;
        _playExternal = external;
        LaunchCoverImage.Visibility = Visibility.Collapsed; // 起動が完了したのでカバー画像は片付ける
        _playLastSampleUtc = DateTime.UtcNow;
    }

    /// <summary>約150msごとに呼ばれ、エミュレーションが動いていた時間を積算する(libretroのみ)。</summary>
    private void SamplePlayTime()
    {
        if (!_playStarted || _playExternal) return;

        DateTime now = DateTime.UtcNow;
        double delta = _playLastSampleUtc is DateTime last ? (now - last).TotalSeconds : 0;
        _playLastSampleUtc = now;

        // スリープ復帰・UIの長い停止で実際より大きく増えないよう、1回の加算に上限を設ける。
        if (_emuHost is { IsPaused: false } && delta > 0)
        {
            _playActiveSeconds += Math.Min(delta, 2.0);
        }
    }

    /// <summary>プレイ結果をRomFileへ記録する。何度呼んでも記録は1回だけ。</summary>
    private void FinishPlaySession()
    {
        if (!_playStarted || _playRecorded || _game is null) return;
        _playRecorded = true;
        if (_game.IsBiosBoot) return; // 本体(BIOS)起動はプレイ記録の対象外

        double seconds;
        if (_playExternal)
        {
            seconds = (DateTime.UtcNow - _externalStartedUtc).TotalSeconds;
        }
        else
        {
            SamplePlayTime();
            seconds = _playActiveSeconds;
        }

        if (seconds < MinRecordedPlaySeconds) return;

        int addMinutes = (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
        DateTimeOffset endedAt = DateTimeOffset.Now;

        // 画面(ゲーム一覧)側が同じRomFileを使い続けても、最近遊んだ順などに反映されるよう
        // メモリ上のRomFileも更新する。
        _game.LastPlayed = endedAt;
        _game.PlayTimeMinutes += addMinutes;

        // 戻った先のゲーム一覧がDBから読み直すとき、記録が間に合っていないことがないよう、
        // ここは完了を待つ(1回のUPDATEだけなので短時間)。失敗してもゲームの終了は妨げない。
        int no = _game.No;
        try
        {
            Task.Run(() => GameLibraryFacade.RecordPlayAsync(no, endedAt, addMinutes)).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePlayPage] プレイ記録の保存に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// EmulationHostを止め、後始末をする。ページを離れるとき、致命的エラー発生時、
    /// 「タイトル選択にもどる」実行時のいずれからも呼ばれる。
    /// </summary>
    private void TeardownEmulation() => TeardownEmulationCore(stopOffUiThread: false).GetAwaiter().GetResult();

    /// <summary>
    /// TeardownEmulation のUIを止めない版。コアの後始末(SRAM書き出し・アンロード等)を
    /// 待つ間もUIスレッドを塞がないので、「タイトル選択にもどる」での戻りが引っかかりにくい。
    /// </summary>
    private Task TeardownEmulationAsync() => TeardownEmulationCore(stopOffUiThread: true);

    private async Task TeardownEmulationCore(bool stopOffUiThread)
    {
        System.Diagnostics.Debug.WriteLine($"[GamePlayPage] TeardownEmulation: 開始 (_emuHost={(_emuHost is null ? "null" : "あり")})");
        _layoutPollTimer.Stop();
        _guideIntroTimer?.Stop();
        _guideIntroTimer = null;
        _guideAnimDip = null;

        // エミュレーションを止める前に、プレイ時間と最終プレイ日時をDBへ記録する(1回だけ)。
        FinishPlaySession();

        if (AppServices.MainWindow is { } window)
        {
            window.WindowBoundsChanged -= OnWindowBoundsChanged;
            window.MinimizedStateChanged -= OnMainWindowMinimizedStateChanged;
            window.AppActiveStateChanged -= OnMainWindowAppActiveStateChanged;
        }

        CleanupExternalApp();

        if (_emuHost is null) return;

        _emuHost.StatusChanged -= OnEmuStatusChanged;
        _emuHost.CoreReady -= OnEmuCoreReady;
        _emuHost.GeometryChanged -= OnEmuGeometryChanged;
        _emuHost.MenuToggleRequested -= OnEmuMenuToggleRequested;
        _emuHost.SystemPanelRequested -= OnEmuSystemPanelRequested;
        _emuHost.RewindToggleRequested -= OnEmuRewindToggleRequested;
        _emuHost.RewindBecameUnavailable -= OnEmuRewindBecameUnavailable;
        _emuHost.FastForwardMultiplierChanged -= OnEmuFastForwardMultiplierChanged;
        _emuHost.PausedMenuButtonPressed -= OnPausedMenuButtonPressed;
        _emuHost.PausedMenuButtonLongPressed -= OnPausedMenuButtonLongPressed;
        _emuHost.PausedMenuButtonRepeated -= OnPausedMenuButtonPressed;
        EmulationHost stopping = _emuHost;
        _emuHost = null;
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        if (stopOffUiThread) await Task.Run(stopping.Stop);
        else stopping.Stop();
        // 終了時の引っかかりの調査用。2秒(=Stopの待ち上限)に近いときは、コアの後始末が重いか詰まっている。
        EmulatorLauncher.Services.AppLog.Info("Play", $"エミュレーションの停止にかかった時間: {stopWatch.ElapsedMilliseconds} ms");

        // EmulationHostがSDLイベントポンプの所有権を手放したので、
        // メニュー画面用の SdlGamepadService を再開する。
        AppServices.Gamepad?.Start();
        System.Diagnostics.Debug.WriteLine("[GamePlayPage] TeardownEmulation: 完了(メニュー用ゲームパッドを再開)");
    }

    // ==================================================================
    // IControllerPage
    // ==================================================================
    // 通常、ゲーム実行中(EmulationHostがSDLイベントポンプを所有している間)は
    // メニュー用のSdlGamepadServiceが停止しているため、これらが呼ばれることはない。
    // EmulationHostの起動に失敗してSdlGamepadServiceが再開された場合にのみ意味を持ち、
    // その状態から「戻る」操作だけができるようにしておく。

    public void OnGamepadButton(GamepadButton button)
    {
        if (button == GamepadButton.B && _emuHost is null && Frame.CanGoBack)
        {
            GoBackWithCoverAnimation();
        }
    }

    public void OnGamepadButtonLongPress(GamepadButton button)
    {
        // このページでは
        //
        //
        // アクションなし
    }
}