using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EmulatorLauncher.Input;
using EmulatorLauncher.Services;
using EmulatorLauncher.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace EmulatorLauncher;

/// <summary>
/// アプリのメインウィンドウ。
/// ・画面遷移用の Frame をホストする
/// ・SdlGamepadService(SDL3ベース)から入力を受け取り、現在表示中のページ
///   (IControllerPage実装)に中継する。ゲーム実行中(GamePlayPage)は、
///   SdlGamepadServiceのポーリングを一時停止し、EmulationHostの専用スレッドへ
///   SDLイベントポンプの所有権を明け渡す(同時に2箇所からSDL_PumpEventsを
///   呼ばないようにするため)。
/// </summary>
public sealed partial class MainWindow : Window
{
    public SdlGamepadService Gamepad { get; }

    /// <summary>このウィンドウのHWND。GamePlayPageがSDL描画用の子ウィンドウを埋め込む際に使う。</summary>
    public nint Hwnd { get; }

    // ウィンドウの位置・サイズ変化は、AppWindow.Changed(WinUI3側のXAMLレイアウト確定を
    // 待って発生する)ではなく、Win32のウィンドウメッセージ(WM_SIZE等)を直接フックする
    // NativeWindowSizeWatcherで検知する。最大化/リストア(元に戻す)はWin32レベルでの
    // HWNDリサイズがXAML側のレイアウト確定より先に起こるため、AppWindow.Changedや
    // XAML側のSizeChanged/CompositionTarget.Renderingで判定すると、その時点ではまだ
    // 古いサイズしか読めないことがある。
    private readonly NativeWindowSizeWatcher _sizeWatcher;

    /// <summary>
    /// ウィンドウの位置・サイズが変わったときに発生する(GamePlayPageのレイアウト再計算用)。
    /// Win32のウィンドウメッセージを直接フックしているため、WinUI3のXAMLレイアウトが
    /// 確定するのを待たずに発生する。
    /// </summary>
    public event EventHandler? WindowBoundsChanged;

    /// <summary>
    /// ウィンドウの最小化状態が変化したときに発生する(true=最小化された, false=復元された)。
    /// GamePlayPageが、SDL描画用の"所有された(owned)"ネイティブウィンドウの表示/非表示を
    /// 連動させるために使う(オーナーウィンドウを最小化しても、所有ウィンドウは
    /// Win32の仕様上自動的には隠れないため)。
    /// </summary>
    public event Action<bool>? MinimizedStateChanged;

    /// <summary>
    /// このアプリ全体のアクティブ状態が変化したときに発生する
    /// (true=アプリがアクティブになった, false=他のアプリへ切り替わって非アクティブになった)。
    /// GamePlayPageが、ゲーム実行中にウィンドウが非アクティブな間、キーボード/マウス操作が
    /// 意図せずゲーム側(SDLの入力ポーリング)へ渡ってしまうのを防ぐために使う。
    /// </summary>
    public event Action<bool>? AppActiveStateChanged;

    public MainWindow()
    {
        this.InitializeComponent();
        this.Title = "RetroNexus";

        Hwnd = WindowNative.GetWindowHandle(this);

        TryResizeWindow(1280, 720);
        TrySetWindowIcon();

        Gamepad = new SdlGamepadService(new DispatcherQueueUiPollingTimer(DispatcherQueue));
        Gamepad.ButtonPressed += OnGamepadButtonPressed;
        Gamepad.ButtonLongPressed += OnGamepadButtonLongPressed;
        Gamepad.ButtonRepeated += OnGamepadButtonRepeated;
        Gamepad.Start();
        StartKeyboardPolling();
        StartPointerInput();

        // アプリ全体で1つの SdlGamepadService / MainWindow を参照できるようにしておく
        AppServices.Gamepad = Gamepad;
        AppServices.MainWindow = this;

        _sizeWatcher = new NativeWindowSizeWatcher(Hwnd);
        _sizeWatcher.SizeOrPositionChanged += () => WindowBoundsChanged?.Invoke(this, EventArgs.Empty);
        _sizeWatcher.MinimizedStateChanged += isMinimized => MinimizedStateChanged?.Invoke(isMinimized);
        _sizeWatcher.AppActiveStateChanged += isActive =>
        {
            _appActive = isActive;
            if (!isActive) ShowCursorIfHidden();
            AppActiveStateChanged?.Invoke(isActive);
        };

        this.Closed += (_, _) =>
        {
            _windowClosed = true;
            _keyTimer?.Stop(); // 終了中にWindow.Contentを読むとExecutionEngineExceptionになるため
            _cursorIdleTimer?.Stop();
            ShowCursorIfHidden();
            _sizeWatcher.Dispose();
        };

        // BGM: メニュー画面では再生し、ゲーム画面(GamePlayPage)の間は一時停止する。
        // ダークテーマ: 表示する画面に合わせて共通の配色を切り替える(ページが作られる前に行う)。
        var lightBackdrop = RootBackdrop.Background;
        var darkBackdrop = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x0D, 0x0E, 0x11));
        RootFrame.Navigating += (_, e) =>
        {
            AppThemeRouter.ApplyForPage(e.SourcePageType);
            RootBackdrop.Background = AppThemeRouter.IsDarkActive ? darkBackdrop : lightBackdrop;
        };
        RootFrame.Navigated += (_, e) =>
        {
            if (e.Content is GamePlayPage) ShowCursorIfHidden();
            if (e.Content is GamePlayPage) BgmPlayer.Pause();
            else BgmPlayer.Resume();
        };
        BgmPlayer.SetVolume(EmulatorLauncher.Emulation.AppPreferencesStore.Load().MenuBgmVolume);
        BgmPlayer.SetEnabled(EmulatorLauncher.Emulation.AppPreferencesStore.Load().MenuBgmEnabled);
        SoundEffects.Enabled = EmulatorLauncher.Emulation.AppPreferencesStore.Load().SoundEffectsEnabled;
        SoundEffects.Volume = EmulatorLauncher.Emulation.AppPreferencesStore.Load().SoundEffectsVolume / 100.0;

        RootFrame.Navigate(AppThemeRouter.ConsoleSelectPageType);
    }

    /// <summary>ウィンドウ左上・タスクバー・Alt+Tabのアイコンを設定する(exe自体のアイコンはcsprojのApplicationIcon)。</summary>
    private void TrySetWindowIcon()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (!System.IO.File.Exists(path)) return;
            var windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
            AppWindow.GetFromWindowId(windowId)?.SetIcon(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] アイコン設定に失敗: {ex.Message}");
        }
    }

    private void TryResizeWindow(int width, int height)
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        if (appWindow is not null)
        {
            // Resize は枠・タイトルバーを含む外寸になり、クライアント領域が指定値より小さくなる。
            // 最小サイズ(NativeWindowSizeWatcher.MinClientWidth/Height)と同じくクライアント基準で合わせる。
            appWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, height));
        }
    }

    /// <summary>
    /// 現在フルスクリーン表示(AppWindowPresenterKind.FullScreen)中かどうか。
    /// トップ画面のシステムアイコンバー、およびゲーム中断メニュー「設定」内の
    /// 「フルスクリーン」トグルの表示テキストを、実際のウィンドウ状態と一致させるために使う。
    /// </summary>
    public bool IsFullScreen
    {
        get
        {
            var windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            return appWindow is not null && appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        }
    }

    /// <summary>
    /// タイトルバー・タスクバーも含めて画面全体を覆う「フルスクリーン」表示をトグルする。
    /// 通常の最大化(OverlappedPresenter.Maximize)はタイトルバーやタスクバーが表示されたままの
    /// ため、それらも隠すにはAppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)で
    /// プレゼンターそのものをFullScreenPresenterへ切り替える必要がある。元に戻すときは
    /// Defaultへ切り替える(切り替え前のOverlappedPresenterの状態(通常サイズ/最大化)は
    /// Windows側が内部的に保持しており、Defaultへ戻すと直前の状態に復元される)。
    /// </summary>
    public void ToggleFullScreen()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        if (appWindow is null)
        {
            return;
        }

        if (appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Default);
        }
        else
        {
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
    }

    // ---- キーボードでのメニュー操作。フォーカスの有無に左右されないよう、ゲームパッドと同じくタイマーでキーの状態を読む ----
    //   矢印=十字キー / Enter=A(決定) / Esc・Backspace=B(もどる)
    //   F2=X / F3=Y / F4=Menu / F1=システム設定パネル(タッチパッド) / F11・F12=L1・R1 / PageUp・PageDown=L2・R2
    // ゲーム一覧の検索欄は常にフォーカスされていて英字がそのまま入力されるため、英字キーは割り当てない(F11/F12はゲーム中の
    // 早送り/中断メニューのキーと揃えた)。NotInTextBox=true のキーは、文字を打つ欄がフォーカスされている間は効かせない。
    private static readonly (Windows.System.VirtualKey Key, GamepadButton Button, bool Repeat, bool NotInTextBox)[] KeyMap =
    {
        (Windows.System.VirtualKey.Up, GamepadButton.DPadUp, true, false),
        (Windows.System.VirtualKey.Down, GamepadButton.DPadDown, true, false),
        (Windows.System.VirtualKey.Left, GamepadButton.DPadLeft, true, false),
        (Windows.System.VirtualKey.Right, GamepadButton.DPadRight, true, false),
        (Windows.System.VirtualKey.Enter, GamepadButton.A, false, false),
        (Windows.System.VirtualKey.Escape, GamepadButton.B, false, false),
        (Windows.System.VirtualKey.Back, GamepadButton.B, false, true),
        (Windows.System.VirtualKey.F2, GamepadButton.X, false, false),
        (Windows.System.VirtualKey.F3, GamepadButton.Y, false, false),
        (Windows.System.VirtualKey.F4, GamepadButton.Menu, false, false),
        (Windows.System.VirtualKey.F1, GamepadButton.Touchpad, false, false),
        (Windows.System.VirtualKey.F11, GamepadButton.LeftShoulder, false, false),
        (Windows.System.VirtualKey.F12, GamepadButton.RightShoulder, false, false),
        (Windows.System.VirtualKey.PageUp, GamepadButton.LeftTrigger, false, false),
        (Windows.System.VirtualKey.PageDown, GamepadButton.RightTrigger, false, false),
    };

    private static readonly TimeSpan KeyRepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan KeyRepeatInterval = TimeSpan.FromMilliseconds(140);

    private readonly Dictionary<Windows.System.VirtualKey, (DateTime Start, DateTime Last)> _keysHeld = new();
    private DispatcherQueueTimer? _keyTimer;
    private bool _appActive = true;
    private bool _windowClosed;
    private bool _textFocusedPrev;

    private void StartKeyboardPolling()
    {
        _keyTimer = DispatcherQueue.CreateTimer();
        _keyTimer.Interval = TimeSpan.FromMilliseconds(30);
        _keyTimer.Tick += (_, _) => PollKeyboard();
        _keyTimer.Start();
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>文字入力中(名前の編集など)は、キーボードの操作をメニュー操作として扱わない。</summary>
    private bool IsTextInputFocused()
    {
        try
        {
            if (Content?.XamlRoot is not { } root) return false;
            object? focused = FocusManager.GetFocusedElement(root);
            // ゲーム選択画面の検索ボックスは常にフォーカスされている(文字を打つだけで検索できる)ため、
            // Tag="MenuKeys" の入力欄では矢印/Enter/Escをメニュー操作として扱う(文字入力はそのまま効く)。
            if (focused is TextBox { Tag: "MenuKeys" }) return false;
            return focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void PollKeyboard()
    {
        if (_windowClosed) return;

        // 隠していたマウスカーソルは、マウスが動いたら戻す(ウィンドウのイベントが届かない場面でも戻せるよう、ここでも見る)。
        if (_cursorHidden && NativeCursor.GetCursorPos(out NativeCursor.POINT cp)
            && (Math.Abs(cp.X - _hiddenCursorX) > 3 || Math.Abs(cp.Y - _hiddenCursorY) > 3))
        {
            ShowCursorIfHidden();
        }

        DateTime now = DateTime.UtcNow;
        bool textFocused = _appActive && IsTextInputFocused();
        bool anyTextBox = _appActive && IsAnyTextBoxFocused();
        // 直前のtickまで文字入力中だったなら、そのtickの間に押されたEnter/Escは入力側が処理済み(確定/取消)。
        // 確定でフォーカスが外れた直後に、同じキーがメニュー操作として二重に効かないようにする。
        bool textJustEnded = _textFocusedPrev && !textFocused;
        _textFocusedPrev = textFocused;
        bool allowed = _appActive
            && RootFrame.Content is IControllerPage and not GamePlayPage
            && !textFocused
            && !IsKeyDown(Windows.System.VirtualKey.Control)
            && !IsKeyDown(Windows.System.VirtualKey.Menu);

        foreach ((Windows.System.VirtualKey key, GamepadButton button, bool repeat, bool notInTextBox) in KeyMap)
        {
            bool down = IsKeyDown(key);
            bool held = _keysHeld.TryGetValue(key, out var state);

            if (!down)
            {
                if (held) _keysHeld.Remove(key);
                continue;
            }

            if (!allowed || (textJustEnded && !held) || (notInTextBox && anyTextBox))
            {
                // 操作できない間に押されたキー(文字入力を確定したEnter等)は、離されるまで押下として扱わない。
                _keysHeld[key] = (now, now);
                continue;
            }

            if (!held)
            {
                _keysHeld[key] = (now, now);
                FireKeyboardButton(button);
            }
            else if (repeat && now - state.Start >= KeyRepeatDelay && now - state.Last >= KeyRepeatInterval)
            {
                _keysHeld[key] = (state.Start, now);
                FireKeyboardButton(button);
            }
        }
    }

    /// <summary>キーボードの操作を、ランチャー操作のボタンとしてページへ渡す。A/Bの入れ替え設定は適用しない。</summary>
    private void FireKeyboardButton(GamepadButton button) => FireLauncherButton(button, LastInputDevice.Keyboard);

    /// <summary>
    /// キーボード/マウス/タッチの操作を、ランチャー操作のボタンとして現在のページへ渡す。
    /// ページが受け取るのは「A=決定、B=もどる」の役割で、A/Bの入れ替え設定は適用しない
    /// (ゲームパッドの物理ボタンだけが入れ替え対象)。
    /// </summary>
    private void FireLauncherButton(GamepadButton button, LastInputDevice source)
    {
        InputDeviceTracker.Report(source);
        if (source == LastInputDevice.Keyboard) HideCursorIfInside();
        if (RootFrame.Content is not IControllerPage page) return;
        PlayMoveSound(button);
        PlayAcceptBackSound(button);
        page.OnGamepadButton(button);
    }

    /// <summary>操作ガイド(HintBar)のクリック/タップなど、画面上のボタンからランチャー操作を渡す。ゲーム中・文字入力中は何もしない。</summary>
    public void InjectLauncherButton(GamepadButton button, bool fromTouch)
    {
        if (!CanUseLauncherInput()) return;
        FireLauncherButton(button, fromTouch ? LastInputDevice.Touch : LastInputDevice.Mouse);
    }

    /// <summary>キーボード/マウス/タッチをランチャー操作として扱える状態か(ゲーム中と文字入力中は不可)。</summary>
    private bool CanUseLauncherInput()
        => _appActive && RootFrame.Content is IControllerPage and not GamePlayPage && !IsTextInputFocused();

    /// <summary>検索欄を含む、文字を打つ欄にフォーカスがあるか(IsTextInputFocusedと違い、Tag="MenuKeys"の欄も含める)。</summary>
    private bool IsAnyTextBoxFocused()
    {
        try
        {
            if (Content?.XamlRoot is not { } root) return false;
            object? focused = FocusManager.GetFocusedElement(root);
            return focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>メニュー画面(ゲーム画面以外)で十字キー(左スティック含む)の移動を押した/押し続けたときの効果音。</summary>
    private void PlayMoveSound(GamepadButton button)
    {
        if (RootFrame.Content is GamePlayPage) return;

        bool isDirection = button is GamepadButton.DPadUp or GamepadButton.DPadDown
            or GamepadButton.DPadLeft or GamepadButton.DPadRight;
        // L1/R1(ページ送り・スクロール)とL2/R2(年代ジャンプ)は、ゲーム選択画面でも移動として鳴らす。
        bool isShoulderOrTrigger = button is GamepadButton.LeftShoulder or GamepadButton.RightShoulder
            or GamepadButton.LeftTrigger or GamepadButton.RightTrigger;
        bool isGameSelect = RootFrame.Content is GameSelectPage or CollectionGameSelectPage;

        // コンソール選択画面の小サイズ表示では、L1/R1がページ送り。
        bool isConsolePaging = RootFrame.Content is ConsoleSelectPage
            && button is GamepadButton.LeftShoulder or GamepadButton.RightShoulder;

        if (isDirection || (isShoulderOrTrigger && isGameSelect) || isConsolePaging)
            SoundEffects.Play("move");
    }

    /// <summary>決定(A)/戻る(B)の効果音。A/B入れ替え設定後の役割(ランチャー操作上のA/B)で鳴らす。ゲーム画面は対象外。</summary>
    private void PlayAcceptBackSound(GamepadButton translatedButton)
    {
        if (RootFrame.Content is GamePlayPage) return;
        // コンソール選択画面のL2/R2(コンソール・タブの切り替え)も決定音。
        if (RootFrame.Content is ConsoleSelectPage
            && translatedButton is GamepadButton.LeftTrigger or GamepadButton.RightTrigger)
        {
            SoundEffects.Play("accept");
            return;
        }

        if (translatedButton == GamepadButton.A) SoundEffects.Play("accept");
        else if (translatedButton == GamepadButton.B) SoundEffects.Play("back");
    }

    private void OnGamepadButtonPressed(GamepadButton button)
    {
        NoteGamepadInput();
        PlayMoveSound(button);
        PlayAcceptBackSound(TranslateForLauncherNavigation(button));
        if (RootFrame.Content is IControllerPage page)
        {
            page.OnGamepadButton(TranslateForLauncherNavigation(button));
        }
    }

    private void OnGamepadButtonLongPressed(GamepadButton button)
    {
        NoteGamepadInput();
        if (RootFrame.Content is IControllerPage page)
        {
            page.OnGamepadButtonLongPress(TranslateForLauncherNavigation(button));
        }
    }

    private void OnGamepadButtonRepeated(GamepadButton button)
    {
        NoteGamepadInput();
        PlayMoveSound(button);
        if (RootFrame.Content is IControllerPage page)
        {
            page.OnGamepadButton(TranslateForLauncherNavigation(button));
        }
    }

    /// <summary>
    /// コントローラー設定画面(Views.ControllerSettingsPage)の「ランチャー操作」で
    /// 物理A/Bの入れ替えが設定されている場合、このランチャー自身のメニュー操作向けに
    /// 物理A/Bボタンの役割を入れ替える。X/Y等の他のボタンは画面ごとに個別の用途で
    /// 既に使われているため、入れ替え対象にはしない(A/Bの2つだけに限定)。
    /// ゲームプレイ中のRetroPad入力(EmulationHost側)には影響しない。
    /// </summary>
    private static GamepadButton TranslateForLauncherNavigation(GamepadButton button)
    {
        return ControllerMappingService.TranslateForLauncher(button);
    }

    // ---- マウス操作(ホイール=十字キー、右クリック/戻るボタン=B)とマウスカーソルの自動非表示 ----
    // ゲーム中(GamePlayPage)と文字入力中は変換しない。左クリックは各画面のクリック処理に任せる。

    private const int WheelNotch = 120;

    /// <summary>
    /// マウスカーソルの自動非表示(パッド/キーボード操作中と、マウスを3秒動かさないとき)。
    /// ShowCursor/SetCursor/WM_SETCURSORの横取りは、WinUIの画面では効かなかったため、
    /// システムの矢印・手・I形カーソルを透明なものに差し替える方法(SetSystemCursor)にした。
    /// 戻すときはSPI_SETCURSORSで元の設定を読み直す。
    /// </summary>
    private const bool CursorAutoHideEnabled = true;
    private static readonly TimeSpan CursorIdleDelay = TimeSpan.FromSeconds(3);

    private DispatcherQueueTimer? _cursorIdleTimer;
    private bool _cursorHidden;
    private int _cursorHideLogs;
    private int _cursorShowLogs;
    private int _cursorHideSkipLogs;
    private bool _pointerInside;
    private int _hiddenCursorX;
    private int _hiddenCursorY;
    private double _lastMouseX = double.NaN;
    private double _lastMouseY = double.NaN;
    private int _wheelAccum;
    private DateTime _lastWheelFire = DateTime.MinValue;

    private void StartPointerInput()
    {
        UIElement root = RootBackdrop;
        // 子要素が処理済み(Handled)にしたイベントも受け取る。ホイールだけは、ScrollViewerなどが
        // 自前でスクロールした場合は任せるため、未処理のものだけを受け取る。
        root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), true);
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);
        root.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(Root_PointerEntered), true);
        root.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Root_PointerExited), true);
        root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Root_PointerWheelChanged), false);

        NativeCursor.RestoreIfLeftOver();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => NativeCursor.RestoreSystemCursors();

        _cursorIdleTimer = DispatcherQueue.CreateTimer();
        _cursorIdleTimer.Interval = CursorIdleDelay;
        _cursorIdleTimer.IsRepeating = false;
        _cursorIdleTimer.Tick += (_, _) => HideCursorIfInside();
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse) _pointerInside = true;
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        // PointerExitedは子要素の境界を越えるたびに上へ伝わってくる(画面内で別のカードへ動かしただけでも届く)。
        // ウィンドウの外へ出たときだけ「外へ出た」と扱う。そうしないと、境界をまたいで止めたときにタイマーが止まり、隠れなくなる。
        Windows.Foundation.Point pos = e.GetCurrentPoint(RootBackdrop).Position;
        if (pos.X >= 0 && pos.Y >= 0 && pos.X < RootBackdrop.ActualWidth && pos.Y < RootBackdrop.ActualHeight) return;
        _pointerInside = false;
        _cursorIdleTimer?.Stop();
        ShowCursorIfHidden();
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        Windows.Foundation.Point pos = e.GetCurrentPoint(RootBackdrop).Position;
        // 小さな揺れ(マウスに触れただけ・机の振動)ではカーソルを戻さない。
        if (!double.IsNaN(_lastMouseX) && Math.Abs(pos.X - _lastMouseX) <= 3 && Math.Abs(pos.Y - _lastMouseY) <= 3) return;
        _lastMouseX = pos.X;
        _lastMouseY = pos.Y;
        _pointerInside = true;
        OnMouseActivity();
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(RootBackdrop);
        if (point.PointerDeviceType == PointerDeviceType.Touch)
        {
            InputDeviceTracker.Report(LastInputDevice.Touch);
            return;
        }
        if (point.PointerDeviceType != PointerDeviceType.Mouse) return;

        _pointerInside = true;
        OnMouseActivity();

        // 右クリックとマウスの「戻る」ボタン=B(もどる)。
        PointerUpdateKind kind = point.Properties.PointerUpdateKind;
        if ((kind == PointerUpdateKind.RightButtonPressed || kind == PointerUpdateKind.XButton1Pressed)
            && CanUseLauncherInput())
        {
            e.Handled = true;
            FireLauncherButton(GamepadButton.B, LastInputDevice.Mouse);
        }
    }

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => HandleLauncherWheel(e);

    /// <summary>
    /// マウスのホイールをランチャー操作(十字キー)に変換する。ホイール=上下、Shift+ホイール=左右。
    /// 横スクロールのScrollViewerのように、ホイールを自前で横スクロールに使ってしまう領域は、
    /// その中身(ScrollViewerより内側の要素)のPointerWheelChangedからこれを呼ぶ(ScrollViewer本体より先に受け取れるため)。
    /// </summary>
    public void HandleLauncherWheel(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        OnMouseActivity();
        if (!CanUseLauncherInput()) return;

        PointerPointProperties props = e.GetCurrentPoint(RootBackdrop).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;   // 目盛りに満たない回転でも、ScrollViewerの横スクロールには回さない

        // 高精度のホイール(1目盛りが120未満)でも目盛り単位でまとめ、連続して回したときは間引く。
        if (Math.Sign(delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _wheelAccum += delta;
        if (Math.Abs(_wheelAccum) < WheelNotch) return;
        _wheelAccum = 0;

        DateTime now = DateTime.UtcNow;
        if ((now - _lastWheelFire).TotalMilliseconds < 40) return;
        _lastWheelFire = now;

        GamepadButton button;
        if (props.IsHorizontalMouseWheel)
            button = delta > 0 ? GamepadButton.DPadRight : GamepadButton.DPadLeft;
        else if (IsKeyDown(Windows.System.VirtualKey.Shift))
            button = delta > 0 ? GamepadButton.DPadLeft : GamepadButton.DPadRight;   // Shift+ホイール=左右
        else if (RootFrame.Content is EmulatorLauncher.Controls.IHorizontalWheelPage { WheelIsHorizontal: true })
            button = delta > 0 ? GamepadButton.DPadLeft : GamepadButton.DPadRight;   // 横一列の一覧: ホイール上下=左右
        else
            button = delta > 0 ? GamepadButton.DPadUp : GamepadButton.DPadDown;

        e.Handled = true;
        FireLauncherButton(button, LastInputDevice.Mouse);
    }

    /// <summary>マウスを動かした/押したとき: カーソルを戻し、一定時間動かさなければまた隠す。</summary>
    private void OnMouseActivity()
    {
        InputDeviceTracker.Report(LastInputDevice.Mouse);
        ShowCursorIfHidden();
        if (_cursorIdleTimer is null) return;
        _cursorIdleTimer.Stop();
        _cursorIdleTimer.Start();
    }

    /// <summary>ゲームパッドのボタン操作があったとき: 最後の入力機器を更新し、マウスカーソルを隠す。</summary>
    private void NoteGamepadInput()
    {
        InputDeviceTracker.Report(LastInputDevice.Gamepad);
        HideCursorIfInside();
    }

    private bool CursorHidingAllowed()
        => _appActive && RootFrame.Content is IControllerPage and not GamePlayPage;

    /// <summary>メニュー画面でマウスがウィンドウ内にあるとき、カーソルを隠す(動かせば戻る)。</summary>
    private void HideCursorIfInside()
    {
        if (!CursorAutoHideEnabled || _cursorHidden) return;
        if (!_pointerInside || !CursorHidingAllowed())
        {
            if (_cursorHideSkipLogs < 5)
            {
                _cursorHideSkipLogs++;
                AppLog.Info("Input", $"[カーソル] 隠さなかった: ウィンドウ内={_pointerInside}, 操作可能な画面={CursorHidingAllowed()}");
            }
            return;
        }
        if (!NativeCursor.GetCursorPos(out NativeCursor.POINT p)) return;
        _hiddenCursorX = p.X;
        _hiddenCursorY = p.Y;
        if (!NativeCursor.HideSystemCursors())
        {
            AppLog.Info("Input", "[カーソル] 透明カーソルへの差し替えに失敗しました");
            return;
        }
        _cursorHidden = true;
        if (_cursorHideLogs < 8)
        {
            _cursorHideLogs++;
            AppLog.Info("Input", "[カーソル] 隠した");
        }
    }

    private void ShowCursorIfHidden()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        NativeCursor.RestoreSystemCursors();
        if (_cursorShowLogs < 8)
        {
            _cursorShowLogs++;
            AppLog.Info("Input", "[カーソル] 戻した");
        }
    }

    /// <summary>
    /// システムカーソルの差し替えで隠す。SetSystemCursorは渡したカーソルを消費するため、そのたびに透明なカーソルを作る。
    /// 差し替え中にアプリが落ちても残らないよう、マーカーファイルを置き、次の起動時に戻す(<see cref="RestoreIfLeftOver"/>)。
    /// </summary>
    internal static class NativeCursor
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetSystemCursor(IntPtr hcur, uint id);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        private const uint SPI_SETCURSORS = 0x0057;
        private static readonly uint[] HiddenIds = { 32512 /* 矢印 */, 32649 /* 手 */, 32513 /* I形 */ };
        private static bool _replaced;

        private static string MarkerPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RetroNexus_cursor_hidden.flag");

        private static IntPtr CreateTransparent()
        {
            // 32x32、AND=全部1(背景を透過)、XOR=全部0(何も描かない)
            var and = new byte[32 * 32 / 8];
            Array.Fill(and, (byte)0xFF);
            var xor = new byte[32 * 32 / 8];
            return CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
        }

        public static bool HideSystemCursors()
        {
            if (_replaced) return true;
            try
            {
                bool any = false;
                foreach (uint id in HiddenIds)
                {
                    IntPtr c = CreateTransparent();
                    if (c != IntPtr.Zero && SetSystemCursor(c, id)) any = true;
                }
                if (any)
                {
                    _replaced = true;
                    try { System.IO.File.WriteAllText(MarkerPath, "1"); } catch { }
                }
                return any;
            }
            catch
            {
                return false;
            }
        }

        public static void RestoreSystemCursors()
        {
            if (!_replaced) return;
            _replaced = false;
            try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { }
            try { System.IO.File.Delete(MarkerPath); } catch { }
        }

        /// <summary>前回、隠したまま落ちた場合に備えて、起動時に呼ぶ。</summary>
        public static void RestoreIfLeftOver()
        {
            try
            {
                if (!System.IO.File.Exists(MarkerPath)) return;
                SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
                System.IO.File.Delete(MarkerPath);
            }
            catch { }
        }
    }
}

/// <summary>
/// <see cref="IUiPollingTimer"/>のWinUI3向け実装。WinUIの<see cref="DispatcherQueueTimer"/>を
/// ラップするだけのアダプター。EmulatorLauncher.Inputライブラリ自体はWinUIに依存させたくないため、
/// この実装はライブラリ側ではなくWinUI3側であるこのプロジェクトに置いている。
/// </summary>
public sealed class DispatcherQueueUiPollingTimer : IUiPollingTimer
{
    private readonly DispatcherQueueTimer _timer;

    public DispatcherQueueUiPollingTimer(DispatcherQueue dispatcherQueue)
    {
        _timer = dispatcherQueue.CreateTimer();
    }

    public void Configure(TimeSpan interval, Action onTick)
    {
        _timer.Interval = interval;
        _timer.Tick += (_, _) => onTick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}

/// <summary>
/// ページ間で共有したいサービスの簡易的なサービスロケーター。
/// 本格的な運用では DI コンテナ (Microsoft.Extensions.DependencyInjection 等) への置き換えを推奨。
/// </summary>
public static class AppServices
{
    public static SdlGamepadService? Gamepad { get; set; }
    public static MainWindow? MainWindow { get; set; }
}