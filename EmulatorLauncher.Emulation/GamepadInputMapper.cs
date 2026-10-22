using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EmulatorLauncher.Input;
using EmulatorLauncher.Interop;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// ゲームプレイ中のゲームパッド/キーボード入力を担当するクラス。<see cref="EmulationHost"/>の
/// 内部分割(4回目)の一環として、単なるファイル分割ではなく実際に独立したクラスとして
/// 切り出した。ゲームパッドの接続/切断検知、RetroPad(JOYPAD/ANALOG)へのマッピング、
/// キーボードフォールバック、および中断メニュー/巻き戻し画面を開くための
/// ZL+ZR同時押し検出(タップ/ホールド判定)をすべてここに集約する。
///
/// SDL_EVENT_QUIT の検知(スレッド終了)はEmulationHost自身のライフサイクル管理の
/// 一部なので、このクラスの<see cref="HandleDeviceEvent"/>には含めず、呼び出し元
/// (EmulationHost.RunLoop / InputPollCallback)側でイベントポンプのループ中に
/// 直接チェックしてもらう。
/// </summary>
internal sealed class GamepadInputMapper
{
    private readonly Action<string> _reportStatus;

    // コントローラーの割り当て(1P/2Pの2枠。接続順・切断時の扱い)はGamepadSlotsが管理する。
    // ここでは、ゲームの入力(retro_input_state_t)で枠ごとのハンドルを読み、
    // ゲーム以外の操作(中断メニュー/早送りのコンボ等)は1P(枠0)だけで受け付ける。
    private static nint Pad1 => GamepadSlots.GetHandle(0);
    private byte[] _keyState = Array.Empty<byte>();

    // 1フレーム分の各枠(1P/2P)のパッド状態。retro_input_pollのたびに CapturePads() で1回だけ読み、
    // retro_input_state(InputState)やホットキー判定はすべてこの値を見る
    // (同じフレーム内で、問い合わせのたびにSDLを直接読んで状態が食い違うのを避けるため)。
    private readonly PadSnapshot[] _snaps = new PadSnapshot[GamepadSlots.MaxSlots];

    // --- RETRO_DEVICE_ANALOG の index/id (libretro.h の定数そのまま) ---
    private const uint RETRO_DEVICE_ID_JOYPAD_MASK = 256;
    // トリガー軸(0..32767)をデジタルのL2/R2として扱う閾値(約25%)。
    private const short TriggerPressThreshold = 8000;
    private const uint RETRO_DEVICE_INDEX_ANALOG_LEFT = 0;
    private const uint RETRO_DEVICE_INDEX_ANALOG_RIGHT = 1;
    private const uint RETRO_DEVICE_ID_ANALOG_X = 0;
    private const uint RETRO_DEVICE_ID_ANALOG_Y = 1;

    // アナログスティックをデジタル方向として扱う際のデッドゾーン
    // (SDL_GetGamepadAxisは -32768〜32767 の範囲)。
    // 小さすぎるとニュートラル付近のノイズで誤反応し、大きすぎると
    // 斜め入力が拾いにくくなるため、実測上ちょうど良い値として8000前後を採用。
    private const short AnalogStickDeadzone = 8000;

    // コアがRETRO_DEVICE_ANALOGでスティックの値を問い合わせてきたポート。
    // PSP/GameCube/N64/PS1(アナログ)等、スティックと十字キーを別の操作として使うコアは、
    // 必ずANALOGで問い合わせる。そのポートでは、左スティックを十字キー(JOYPADの方向)としては
    // 扱わない(スティックを倒すと十字キーも押した扱いになり、二重に入力されるのを防ぐ)。
    // ANALOGを一度も問い合わせないコア(ファミコン等の2Dコア)だけ、スティックで十字キー操作できる。
    private readonly bool[] _coreQueriesAnalog = new bool[16];

    /// <summary>true なら、コアがスティックをANALOGで読んでいても、左スティックを十字キーとしても返す(スティックのアナログ値を方向に変換するのが不完全なコア向け)。</summary>
    public bool StickAlwaysAsDpad { get; set; }

    // --- 中断メニュー/巻き戻し画面を開くための ZL+ZR 同時押し検出 ---
    private static readonly TimeSpan MenuComboLongPressThreshold = TimeSpan.FromMilliseconds(600);

    // SDL3のL2/R2はアナログ軸(0=離した〜32767=全押し)として提供されるため、
    // 「押した」とみなす閾値。半分を超えたら押下とみなす
    // (Services/GamepadEdgeTrackerのメニュー操作用トリガー判定と同じ値)。
    private const short MenuComboTriggerThreshold = 16000;
    private bool _menuTogglePrevCombo;
    private bool _touchpadPrev;
    private static readonly GamepadButton[] TouchpadCombo = { GamepadButton.Touchpad };
    private bool _rewindPrevCombo;
    private DateTime _menuComboPressStart;
    private bool _menuComboLongPressFired;

    // --- 早送り倍率サイクル切り替えのための R(右肩ボタン)+ZR(右トリガー)同時押し検出 ---
    // ZL+ZRの中断メニュー/巻き戻しコンボとは物理ボタンの組み合わせが異なる
    // (こちらは右肩ボタン=RIGHT_SHOULDERと右トリガー=RIGHT_TRIGGER軸の組み合わせ)ため、
    // 押下判定を混同することはない。早送りは「タップで切り替え、離すまで待つ必要は無い」
    // 操作なので、ZL+ZRのようなタップ/ホールドの区別は行わず、押し始めた瞬間(エッジ)に
    // 毎回1回だけ切り替える。
    private bool _fastForwardComboPrevPressed;
    // 早送りの組み合わせが中断/巻き戻しの組み合わせの一部のとき(R2 と L2+R2 など)、押した時点では決めず、離したときに発火する。
    private bool _fastForwardPending;

    // --- 早送り倍率を逆方向にサイクルするための L(左肩ボタン)+ZL(左トリガー)同時押し検出 ---
    // R+ZR(順方向)と対になる操作。ZL+ZR(中断メニュー/巻き戻し)とはこちらも
    // 物理ボタンの組み合わせが異なる(LEFT_SHOULDER + LEFT_TRIGGERであり、
    // LEFT_TRIGGER + RIGHT_TRIGGERのZL+ZRコンボとは別)ため、通常の使用で誤って
    // 同時に発火することは無い(理論上ZL+ZR+Lを同時に押せば両方のコンボが同時に
    // 発火しうるが、これはR+ZR側についても元々同じ性質であり、早送りの切り替えは
    // 何度発火しても実害の無い操作なので許容する)。
    private bool _fastForwardReverseComboPrevPressed;

    /// <summary>
    /// RetroPadのボタン名("A"/"B"/"X"/"Y"/"L"/"R"/"Start"/"Select") → 物理ボタン名
    /// (GamepadButtonの名前の文字列)の対応表。EmulationHost.ButtonMappingから転送される。
    /// </summary>
    public Dictionary<string, string> ButtonMapping
    {
        get => _buttonMapping;
        set
        {
            _buttonMapping = value ?? new Dictionary<string, string>();
            RebuildMappingCache();
        }
    }

    private Dictionary<string, string> _buttonMapping = new();

    // ButtonMappingを起動時に一度だけ解釈した結果(毎フレーム文字列を解析しないため)。
    // 値がnullなら割り当てなし("None")。方向入力のキーは、割り当てが無ければ既定の物理入力が入る。
    private readonly Dictionary<string, GamepadButton?> _resolvedMapping = new(StringComparer.Ordinal);
    private bool _leftStickRemapped;
    private bool _rightStickRemapped;

    private void RebuildMappingCache()
    {
        _resolvedMapping.Clear();
        foreach (string key in ControllerMappingService.DirectionKeys)
        {
            _resolvedMapping[key] = ControllerMappingService.GetBuiltinPhysicalButton(key);
        }

        foreach (KeyValuePair<string, string> kv in _buttonMapping)
        {
            // 十字キー・左スティックの方向(Up/LSUp等)の再割り当ては廃止した(設定画面からは変えられない)。
            // 以前の試用などで保存データに残っていても、無視して常に既定(同じ名前の物理入力)にする。
            // (残っていると、左右スティックが入れ替わる/反転するなどの原因になる)
            // 右スティックの方向(RSUp等)だけは、プリセットが「右スティック↑」などをゲームのボタン
            // (例: N64のCボタン)として持つので、設定画面での割り当てに従う。
            if (ControllerMappingService.IsDirectionKey(kv.Key) && !IsRightStickKey(kv.Key)) continue;

            _resolvedMapping[kv.Key] = string.Equals(kv.Value, "None", StringComparison.OrdinalIgnoreCase)
                ? null
                : GamepadPhysicalButtonReader.ParseOrDefault(kv.Value, ControllerMappingService.GetBuiltinPhysicalButton(kv.Key));
        }

        // 左スティックは常に生の値を返す。右スティックは、方向の割り当てが既定(同名の物理入力)から
        // 変わっているときだけ、割り当てに従ってアナログ値を組み立てる(割り当てなしの方向は0)。
        _leftStickRemapped = false;
        _rightStickRemapped = false;
        foreach (string key in ControllerMappingService.DirectionKeys)
        {
            if (!IsRightStickKey(key)) continue;
            if (!_resolvedMapping.TryGetValue(key, out GamepadButton? mapped) || mapped != ControllerMappingService.GetBuiltinPhysicalButton(key))
            {
                _rightStickRemapped = true;
                break;
            }
        }
    }

    private static bool IsRightStickKey(string key) => key.StartsWith("RS", StringComparison.Ordinal);

    /// <summary>
    /// ゲーム中の呼び出し操作(中断メニュー/巻き戻し/早送り)の組み合わせ。GamePlayPageが起動時に
    /// そのコンソールの設定(ControllerMappingService.ResolveHotkeys)を設定する。
    /// 1P・2Pどちらのコントローラーで押しても有効(組み合わせの全ボタンが同じコントローラーで押されていること)。
    /// </summary>
    public ControllerMappingService.HotkeyBindings Hotkeys { get; set; } = new();

    /// <summary>
    /// 連射の割り当て(RetroPadのキー("A"等) → 物理ボタン名)。その物理ボタンを押している間だけ、
    /// そのキーを1秒間に <see cref="TurboRateHz"/> 回の押す/離すにする(ControllerMappingService.ResolveTurbo)。
    /// 通常の割り当て(ButtonMapping)とは別に働き、どちらかが押されていればそのキーは押された扱いになる。
    /// </summary>
    public Dictionary<string, string> TurboMapping
    {
        get => _turboMapping;
        set
        {
            _turboMapping = value ?? new Dictionary<string, string>();
            _turboResolved.Clear();
            foreach (KeyValuePair<string, string> kv in _turboMapping)
            {
                if (Enum.TryParse(kv.Value, out GamepadButton physical)) _turboResolved[kv.Key] = physical;
            }
        }
    }

    private Dictionary<string, string> _turboMapping = new();
    private readonly Dictionary<string, GamepadButton> _turboResolved = new(StringComparer.Ordinal);

    /// <summary>
    /// ホスト側から「押した」ことにするボタン(ビット N = RetroJoypad の id N)。1P(ポート0)だけに効く。
    /// ディスクシステムのように、コアがディスク入れ替えをL/Rボタン入力で受け付ける場合に、
    /// 中断メニューの操作からボタン押下を代行するために使う(EmulationHost.Disk.cs)。
    /// </summary>
    public volatile uint VirtualPressMask;

    /// <summary>
    /// コアへ渡してよい物理ボタン(ビット N = RetroJoypad の id N)。既定は全ボタン。
    /// ファミコン(ディスクシステム)のように、存在しないボタンを渡すとコアが別の意味に使ってしまう
    /// 場合に、許可リスト以外の物理入力を未入力として扱う。VirtualPressMask(ホストの代行入力)は対象外。
    /// </summary>
    public uint AllowedButtonMask { get; set; } = 0xFFFF;

    /// <summary>連射の速さ(1秒間に押す回数)。</summary>
    public const int TurboRateHz = 15;

    /// <summary>1PのゲームパッドのSDLハンドル(未接続なら nint.Zero)。メニュー操作などゲーム以外の入力に使う。</summary>
    public nint Gamepad => Pad1;

    /// <summary>
    /// ゲーム実行中に中断メニューを開く操作(タップ)が検出されたときに発生する。
    /// EmulationHost側で同名の公開イベントへ転送される。
    /// </summary>
    public event EventHandler? MenuToggleRequested;

    /// <summary>ゲーム実行中にタッチパッド(PS4/PS5)が押されたときに発生する(システム設定パネルを開く操作)。</summary>
    public event EventHandler? SystemPanelRequested;

    /// <summary>
    /// ゲーム実行中に巻き戻し画面を開く操作(ホールド)が検出されたときに発生する。
    /// EmulationHost側で同名の公開イベントへ転送される。
    /// </summary>
    public event EventHandler? RewindToggleRequested;

    /// <summary>
    /// ゲーム実行中にR(右肩ボタン)+ZR(右トリガー)同時押しが「新たに」検出された
    /// (押していない状態から押した状態になった)瞬間に発生する。早送り倍率の
    /// 順方向サイクル切り替え(EmulationHost側)に使う。
    /// </summary>
    public event EventHandler? FastForwardComboPressed;

    /// <summary>
    /// ゲーム実行中にL(左肩ボタン)+ZL(左トリガー)同時押しが「新たに」検出された
    /// 瞬間に発生する。早送り倍率の逆方向サイクル切り替え(EmulationHost側)に使う。
    /// FastForwardComboPressed(R+ZR、順方向)と対になる。
    /// </summary>
    public event EventHandler? FastForwardReverseComboPressed;

    public GamepadInputMapper(Action<string> reportStatus)
    {
        _reportStatus = reportStatus;
        RebuildMappingCache();   // ButtonMappingが設定されなくても、十字キー等の既定の割り当てが効くように
    }

    /// <summary>
    /// すでに接続済みのコントローラーを(起動後に一度だけ)列挙して、空いている枠へ割り当てる
    /// (起動時、接続イベントが拾えなかった場合用)。割り当ての内容はGamepadSlotsが決める。
    /// </summary>
    public void OpenFirstAvailable()
    {
        foreach (string message in GamepadSlots.Reconcile()) _reportStatus(message);
        foreach (string message in GamepadSlots.EnsureInitialEnumeration()) _reportStatus(message);
    }

    /// <summary>
    /// ゲームパッドの接続/切断イベントを処理する(割り当てはGamepadSlotsが行う)。
    /// SDL_EVENT_QUITはここでは扱わない(呼び出し元がイベントポンプのループ中に直接チェックする)。
    /// </summary>
    public void HandleDeviceEvent(Sdl.SDL_Event ev)
    {
        string? message = GamepadSlots.HandleDeviceEvent(ev);
        if (message is null) return;

        _reportStatus(message);
        if (ev.type == Sdl.SDL_EVENT_GAMEPAD_REMOVED && GamepadSlots.ConnectedCount == 0)
        {
            _reportStatus("コントローラが切断されました(キーボード操作に切替)");
        }
    }

    public string Describe()
    {
        string? p1 = GamepadSlots.DescribeSlot(0);
        string? p2 = GamepadSlots.DescribeSlot(1);

        if (p1 is null && p2 is null)
            return "コントローラ未接続(キーボード操作)";

        return (p1, p2) switch
        {
            (not null, not null) => $"コントローラ: 1P {p1} / 2P {p2}",
            (not null, null) => $"コントローラ: {p1}",
            _ => $"コントローラ: 2P {p2}(1Pは未接続)",
        };
    }

    /// <summary>
    /// 中断メニューを開くたびに呼ぶ。前回のポーズ時の押しっぱなし状態(タップ/ホールド判定の
    /// 途中状態)を引きずらないようにする。
    /// </summary>
    public void ResetMenuCombo()
    {
        _menuTogglePrevCombo = false;
        _rewindPrevCombo = false;
        _menuComboLongPressFired = false;
    }

    /// <summary>
    /// 中断メニューを開くたびに呼ぶ(ResetMenuComboと同じタイミング)。メニューを
    /// 開いている間はDetectFastForwardComboが呼ばれない(InputPollCallbackはcore.Run()
    /// 経由でしか呼ばれず、一時停止中はcore.Run()自体が止まるため)ので厳密には必須では
    /// ないが、念のためメニューを開くたびに押しっぱなし状態をリセットしておく
    /// (メニューを開いている間にR+ZRを押したまま離してから再開すると、押し始めの
    /// エッジを取りこぼして早送りが切り替わらなくなる、といった取りこぼしを防ぐため)。
    /// </summary>
    public void ResetFastForwardCombo()
    {
        _fastForwardComboPrevPressed = false;
        _fastForwardPending = false;
    }

    /// <summary>ResetFastForwardComboと同じタイミングで呼ぶ。L+ZL(逆方向)版。</summary>
    public void ResetFastForwardReverseCombo()
    {
        _fastForwardReverseComboPrevPressed = false;
    }

    /// <summary>
    /// エミュレーション終了時の後始末。コントローラーの割り当て(GamepadSlots)はアプリ全体で
    /// 持ち続け、メニュー操作へそのまま引き継ぐため、ここではコントローラーを閉じない。
    /// </summary>
    public void Close()
    {
    }

    /// <summary>SDL_GetKeyboardStateの内容をキャッシュする(retro_input_poll_t内から毎フレーム呼ばれる)。</summary>
    // ------------------------------------------------------------------
    // マウス入力(RETRO_DEVICE_POINTER / RETRO_DEVICE_MOUSE)。
    // DS/3DSの下画面タッチ用。タッチ領域(下画面の位置)の計算は、レイアウトごとの
    // 配置を知っているコア側が行う。こちらは「ゲーム画面(描画ウィンドウ)の中のどこを
    // 指しているか」を、ウィンドウ全体を-32767〜32767へ正規化した値として渡すだけでよい。
    // ------------------------------------------------------------------

    /// <summary>SDL描画ウィンドウのHWND。EmulationHostがウィンドウ作成後に設定する(0なら無効)。</summary>
    public nint GameWindowHandle { get; set; }

    // コアがPOINTER/MOUSEデバイスを問い合わせたらtrue(以降PollMouseが有効になる)。
    private volatile bool _pointerDeviceUsed;
    private bool _mouseInside;
    private bool _mouseHadPosition;
    private int _mouseLastX, _mouseLastY;
    private short _pointerX, _pointerY;
    private int _mouseDx, _mouseDy;
    private bool _mouseLeft, _mouseRight, _mouseMiddle;

    /// <summary>
    /// retro_input_pollのタイミングで、カーソルがゲーム画面の上にあるかと位置・ボタンを読み取る。
    /// 描画ウィンドウより前面に別のウィンドウがある場合(カーソル下のウィンドウが描画ウィンドウ
    /// でない場合)は、画面外として扱う。エミュレーションスレッドからのみ呼ぶ。
    /// </summary>
    public void PollMouse()
    {
        // コアがPOINTER/MOUSEを一度も問い合わせていない(N64/GC等のパッド専用コア)間は何もしない。
        // 毎フレームWindowFromPointを呼ぶと、他スレッド所有のウィンドウへ同期メッセージを送るため、
        // 3倍速などの高頻度ポーリングでエミュレーションスレッドが詰まる恐れがある。
        if (!_pointerDeviceUsed) return;

        _mouseDx = 0;
        _mouseDy = 0;

        nint hwnd = GameWindowHandle;
        if (hwnd == nint.Zero || !Win32.GetCursorPos(out Win32.POINT pt)
            || Win32.WindowFromPoint(pt) != hwnd)
        {
            _mouseInside = false;
            _mouseHadPosition = false;
            _mouseLeft = _mouseRight = _mouseMiddle = false;
            if (!HoldPointerOutside) _pointerX = _pointerY = 0;
            return;
        }

        Win32.ScreenToClient(hwnd, ref pt);
        if (!Win32.GetClientRect(hwnd, out Win32.RECT rc) || rc.Right <= 0 || rc.Bottom <= 0
            || pt.X < 0 || pt.Y < 0 || pt.X >= rc.Right || pt.Y >= rc.Bottom)
        {
            _mouseInside = false;
            _mouseHadPosition = false;
            _mouseLeft = _mouseRight = _mouseMiddle = false;
            if (HoldPointerOutside && rc.Right > 0 && rc.Bottom > 0)
            {
                // ウィンドウの外へ出たら、マウスの向きの画面端にポインターを留める(Wiiが「ポインター無し」と見て下中央へ動かすのを防ぐ)。
                _pointerX = (short)Math.Clamp((pt.X + 0.5) / rc.Right * 65534.0 - 32767.0, -32767.0, 32767.0);
                _pointerY = (short)Math.Clamp((pt.Y + 0.5) / rc.Bottom * 65534.0 - 32767.0, -32767.0, 32767.0);
            }
            else _pointerX = _pointerY = 0;
            return;
        }

        _mouseInside = true;
        _pointerX = (short)Math.Clamp((pt.X + 0.5) / rc.Right * 65534.0 - 32767.0, -32767.0, 32767.0);
        _pointerY = (short)Math.Clamp((pt.Y + 0.5) / rc.Bottom * 65534.0 - 32767.0, -32767.0, 32767.0);

        if (_mouseHadPosition)
        {
            _mouseDx = pt.X - _mouseLastX;
            _mouseDy = pt.Y - _mouseLastY;
        }
        _mouseLastX = pt.X;
        _mouseLastY = pt.Y;
        _mouseHadPosition = true;

        _mouseLeft = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        _mouseRight = (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0;
        _mouseMiddle = (Win32.GetAsyncKeyState(Win32.VK_MBUTTON) & 0x8000) != 0;
    }

    /// <summary>
    /// true の間、右スティックでマウスカーソル(=タッチ位置)を動かし、R3(割り当て後)をタッチ(押す)として扱う。
    /// DS(melonDS)用。マウスで動かしてもスティックで動かしても、同じカーソルを共有する。EmulationHostが設定する。
    /// </summary>
    public volatile bool StickCursorEnabled;

    private long _stickCursorTicks;
    private double _stickCursorX, _stickCursorY;

    // 右スティックを最大に倒したとき、画面の横幅を約1.1秒で横切る速さ(傾きの2乗で、少し倒したときは細かく動く)。
    private const double StickCursorWidthPerSec = 0.9;

    /// <summary>右スティックで、OSのマウスカーソルを動かす。PollMouseより前に、毎フレームのpoll内から呼ぶ。</summary>
    public void PollStickCursor()
    {
        if (!StickCursorEnabled) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _stickCursorTicks == 0 ? 0 : (now - _stickCursorTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _stickCursorTicks = now;
        if (dt <= 0 || dt > 0.1) return;

        PadSnapshot pad = _snaps[0];
        if (!pad.Connected) return;
        double sx = StickAxis(pad.RightX), sy = StickAxis(pad.RightY);
        if (sx == 0 && sy == 0) return;

        nint hwnd = GameWindowHandle;
        if (hwnd == nint.Zero || !Win32.GetClientRect(hwnd, out Win32.RECT rc) || rc.Right <= 0 || rc.Bottom <= 0) return;
        if (!Win32.GetCursorPos(out Win32.POINT pt)) return;
        Win32.ScreenToClient(hwnd, ref pt);

        // マウスで動かされた/ウィンドウの外にあるときは、現在位置(外なら中央)から始める。
        bool inside = pt.X >= 0 && pt.Y >= 0 && pt.X < rc.Right && pt.Y < rc.Bottom;
        double cx = inside ? pt.X : rc.Right / 2.0;
        double cy = inside ? pt.Y : rc.Bottom / 2.0;
        if (Math.Abs(cx - _stickCursorX) > 1.5 || Math.Abs(cy - _stickCursorY) > 1.5)
        {
            _stickCursorX = cx;
            _stickCursorY = cy;
        }

        double speed = rc.Right * StickCursorWidthPerSec;
        _stickCursorX = Math.Clamp(_stickCursorX + sx * Math.Abs(sx) * speed * dt, 0, rc.Right - 1);
        _stickCursorY = Math.Clamp(_stickCursorY + sy * Math.Abs(sy) * speed * dt, 0, rc.Bottom - 1);

        var target = new Win32.POINT { X = (int)_stickCursorX, Y = (int)_stickCursorY };
        Win32.ClientToScreen(hwnd, ref target);
        Win32.SetCursorPos(target.X, target.Y);
    }

    /// <summary>
    /// RETRO_DEVICE_MOUSEに答えるポート。既定は0(1P側)。コアのポート1にマウスを挿す設定(FM TOWNS等)のときは1にする。
    /// RETRO_DEVICE_POINTERは従来どおりポート0のみ。
    /// </summary>
    public uint MousePort { get; set; }

    /// <summary>RETRO_DEVICE_POINTER(絶対座標)/ RETRO_DEVICE_MOUSE(相対移動+ボタン)の問い合わせ。POINTERは1P(ポート0)のみ、MOUSEはMousePort。</summary>
    private short GetMouseState(uint port, uint device, uint index, uint id)
    {
        _pointerDeviceUsed = true;

        // Wiiの「ジャイロ」: DolphinコアはIRの位置をPOINTER(絶対座標)で読むので、ジャイロの位置を返す。
        if (GyroPointerEnabled && device == RetroDevice.POINTER)
        {
            if (port != 0 || index != 0) return 0;
            return id switch
            {
                0 => (short)_gyroPointerX,
                1 => (short)_gyroPointerY,
                2 => (short)(_mouseInside && _mouseLeft ? 1 : 0),
                _ => 0,
            };
        }

        // ホットキー「ポインターを中央へ」の組み合わせ(ID_MOUSE_BUTTON_4)は、マウスが画面内になくても返す(Dolphinのジャイロ照準の再センタリング用)。
        if (port == 0 && device == RetroDevice.MOUSE && id == 9)
            return (short)(_pointerRecenterHeld ? 1 : 0);

        // Wii「ジャイロ」: マウスの位置は使わず、ポインターは常に画面中央(0,0)・押下なし。
        if (PointerCentered && device == RetroDevice.POINTER) return 0;

        // Wiiのマウス操作: 外にいる間もポインターは画面端に留める(ボタンは押していない扱い)。
        if (HoldPointerOutside && !_mouseInside && port == 0 && device == RetroDevice.POINTER && index == 0)
            return id switch { 0 => _pointerX, 1 => _pointerY, _ => (short)0 };

        // マウス(RETRO_DEVICE_MOUSE)は、MousePortで指定されたポートの問い合わせにだけ答える(通常は1P=ポート0)。
        uint expectedPort = device == RetroDevice.MOUSE ? MousePort : 0u;
        if (port != expectedPort || !_mouseInside) return 0;

        if (device == RetroDevice.POINTER)
        {
            if (index != 0) return 0;
            return id switch
            {
                0 => _pointerX,          // RETRO_DEVICE_ID_POINTER_X
                1 => _pointerY,          // RETRO_DEVICE_ID_POINTER_Y
                2 => (short)(_mouseLeft || (StickCursorEnabled && InputState(0, RetroDevice.JOYPAD, 0, RetroJoypad.R3) != 0) ? 1 : 0), // RETRO_DEVICE_ID_POINTER_PRESSED
                _ => 0,
            };
        }

        // RETRO_DEVICE_MOUSE
        return id switch
        {
            0 => (short)Math.Clamp(_mouseDx, short.MinValue, short.MaxValue), // ID_MOUSE_X(相対)
            1 => (short)Math.Clamp(_mouseDy, short.MinValue, short.MaxValue), // ID_MOUSE_Y(相対)
            2 => (short)(_mouseLeft ? 1 : 0),   // ID_MOUSE_LEFT
            3 => (short)(_mouseRight ? 1 : 0),  // ID_MOUSE_RIGHT
            6 => (short)(_mouseMiddle ? 1 : 0), // ID_MOUSE_MIDDLE
            9 => (short)(_pointerRecenterHeld ? 1 : 0), // ID_MOUSE_BUTTON_4: 「ポインターを中央へ」の組み合わせ(Dolphinのジャイロ照準の再センタリング用)
            _ => 0,
        };
    }

    // ------------------------------------------------------------------
    // ライトガン(RETRO_DEVICE_LIGHTGUN)。Wii(Dolphin)のIRポインタを「ジャイロ」で動かすために使う
    // (コアのオプション「IRポインタの操作方法」=ライトガン)。コントローラーのジャイロ(角速度)を積分して、
    // 画面上の位置(-32767〜32767)を作り、SCREEN_X/SCREEN_Yとして返す。コントローラーを画面へ向けて
    // 構える使い方が前提で、右スティックを使わないため、両方の親指でボタンを押せる。
    // 位置が少しずつずれたときは、ホットキー「ポインターを中央へ」(RequestPointerRecenter)で戻す。
    // ------------------------------------------------------------------

    private const int SDL_SENSOR_ACCEL = 1;
    private const int SDL_SENSOR_GYRO = 2;
    private const uint LIGHTGUN_ID_SCREEN_X = 13;
    private const uint LIGHTGUN_ID_SCREEN_Y = 14;
    private const uint LIGHTGUN_ID_IS_OFFSCREEN = 15;

    // 画面の横いっぱい(65534)を、約40度の首振り(0.70rad)で動かす。縦は同じ角度あたりの移動量を
    // 画面の縦横比(16:9)に合わせて大きくする(縦いっぱいが約22度)。
    // 横は実機確認で動きが小さかったため、0.14rad(約8度)で画面の横いっぱいに変更(縦は従来どおり)。
    private const double GyroUnitsPerRadX = 65534.0 / 0.14;
    // Wii以外(Flycast/FBNeo)のライトガン用: 感度を下げ、横の無視量を大きくして安定させる
    private const double GyroUnitsPerRadXGun = 65534.0 / 0.40;
    private const double GyroYawDeadzoneGun = 0.06;
    private const double GyroUnitsPerRadY = 65534.0 / 0.70 * 16.0 / 9.0;

    // 向きの符号(+1/-1)。SDLのジャイロはX=ピッチ(上を向くと+)、Y=ヨー(左を向くと+)。
    // 実機確認の結果、左右(GyroSignY)は+1が正しい向き。縦(GyroSignX)は-1。逆に動くなら符号を反転する。
    private const double GyroSignX = -1.0;
    private const double GyroSignY = 1.0;

    // この角速度(rad/s)より小さい動きは、止まっているときのノイズとして捨てる。
    private const double GyroNoiseDeadzone = 0.008;
    // 左右の基本の無視量(rad/s)=約1.7°/s、と、上下の速さ1°/sあたりに追加で無視する左右の量(比率)。
    private const double GyroYawDeadzone = 0.03;
    private const double GyroYawCrossFactor = 0.04;
    // 3軸の角速度の大きさがこれ未満の間は「止まっている」とみなし、そのときの値を静止時のずれ(バイアス)として覚える。
    private const double GyroRestThreshold = 0.05;

    private volatile bool _lightgunUsed;
    private volatile bool _pointerRecenterRequested;
    /// <summary>true の間、コアからの右スティック(ANALOG index 1)の問い合わせには常に0を返す。</summary>
    public volatile bool IgnoreRightStickAnalog;
    /// <summary>true の間、POINTER(絶対座標)の問い合わせには常に中央(0,0)・押下なしを返す。</summary>
    public volatile bool PointerCentered;
    /// <summary>true のとき、マウスがウィンドウの外にいる間もポインター位置を画面端に留める(Wiiのマウス操作用)。</summary>
    public volatile bool HoldPointerOutside;
    private volatile bool _pointerRecenterHeld;
    private bool _pointerRecenterPrevPressed;
    private double _gyroPointerX, _gyroPointerY;
    private readonly double[] _gyroBias = new double[3];
    private readonly float[] _gyroBuf = new float[3];
    private readonly float[] _accelBuf = new float[3];
    private readonly double[] _upVec = { 0, 1, 0 };
    private readonly double[] _smoothAccel = new double[3];
    private readonly double[] _stillSum = new double[3];
    private bool _upInit;
    private double _shakiness, _stillTime;
    private int _stillCount;
    private const int SmoothMax = 64;
    private const double GyroSmoothThreshold = 5.0 * Math.PI / 180.0; // rad/s
    private const double GyroSmoothTime = 0.125; // 秒
    private readonly double[] _smoothYaw = new double[SmoothMax], _smoothPitch = new double[SmoothMax];
    private int _smoothHead;

    /// <summary>本体の回転(角速度w[rad/s]、dt秒)のぶんだけ、本体座標で見た固定ベクトルvを逆向きに回す(微小回転)。</summary>
    private static void RotateByInverse(double[] v, double wx, double wy, double wz, double dt)
    {
        double cx = wy * v[2] - wz * v[1];
        double cy = wz * v[0] - wx * v[2];
        double cz = wx * v[1] - wy * v[0];
        v[0] -= cx * dt; v[1] -= cy * dt; v[2] -= cz * dt;
    }
    private long _gyroLastTicks;
    private int _gyroEnablePollCounter;
    private double _dbgYawDeg, _dbgPitchDeg, _dbgDx, _dbgDy;
    private int _dbgFrames;

    /// <summary>ジャイロのポインターを画面の中央へ戻すよう要求する(次のポーリングで反映される)。</summary>
    /// <summary>true の間、POINTER(ポート0)の位置をマウスではなくジャイロから返す(Wiiのオプション「ジャイロ」)。EmulationHostが毎フレーム設定する。</summary>
    public volatile bool GyroPointerEnabled;

    /// <summary>カーソルがゲーム画面の上にあるか(マウス/ポインターが問い合わせられている間のみ有効)。</summary>
    public bool MouseInside => _mouseInside;

    public void RequestPointerRecenter() => _pointerRecenterRequested = true;

    /// <summary>
    /// retro_input_pollのタイミングで、ジャイロの角速度からポインターの位置を更新する。
    /// コアがLIGHTGUNを問い合わせるまでは何もしない。エミュレーションスレッドからのみ呼ぶ。
    /// </summary>
    public void PollGyroPointer()
    {
        if (!GyroPointerEnabled && !(_lightgunUsed && (LightgunWii || LightgunAimMode == 2))) return;

        if (_pointerRecenterRequested)
        {
            _pointerRecenterRequested = false;
            _gyroPointerX = 0;
            _gyroPointerY = 0;
        }

        nint pad = GamepadSlots.GetHandle(0);
        if (pad == nint.Zero)
        {
            _gyroLastTicks = 0;
            return;
        }

        try
        {
            // コア側がジャイロを止めてしまっても読めるよう、約1秒ごとに有効化し直す(既に有効なら何も起きない)。
            if (_gyroEnablePollCounter++ % 60 == 0 && Sdl.SDL_GamepadHasSensor(pad, SDL_SENSOR_GYRO))
                Sdl.SDL_SetGamepadSensorEnabled(pad, SDL_SENSOR_GYRO, true);
            if (_gyroEnablePollCounter % 60 == 1 && Sdl.SDL_GamepadHasSensor(pad, SDL_SENSOR_ACCEL))
                Sdl.SDL_SetGamepadSensorEnabled(pad, SDL_SENSOR_ACCEL, true);

            // ジャイロはSDLのセンサーイベント(コントローラー本体の送信周期。DualSenseは約250Hz)を1つずつ処理する。
            // 以前は1フレーム(60Hz)に1回「最新値」を読むだけだったので、その間の速い動きを取りこぼしていた。
            if (_sensorSamples.Count > 0)
            {
                for (int i = 0; i < _sensorSamples.Count; i++)
                {
                    SensorSample sm = _sensorSamples[i];
                    _gyroBuf[0] = sm.Gx; _gyroBuf[1] = sm.Gy; _gyroBuf[2] = sm.Gz;
                    _accelBuf[0] = sm.Ax; _accelBuf[1] = sm.Ay; _accelBuf[2] = sm.Az;
                    _accelValid = sm.HasAccel;
                    ProcessGyroSample(sm.Dt);
                }
                _sensorSamples.Clear();
                _gyroLastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            else if (!_sensorEventsSeen)
            {
                // イベントが来ない環境向けの予備: 従来どおり1フレームに1回、最新値を読む。
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double dt = _gyroLastTicks == 0 ? 0 : (now - _gyroLastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                _gyroLastTicks = now;
                if (dt <= 0 || dt > 0.1) return;
                if (!Sdl.SDL_GetGamepadSensorData(pad, SDL_SENSOR_GYRO, _gyroBuf, 3)) return;
                _accelValid = Sdl.SDL_GetGamepadSensorData(pad, SDL_SENSOR_ACCEL, _accelBuf, 3);
                ProcessGyroSample(dt);
            }
        }
        catch
        {
            // ジャイロが読めなくても、ゲームの進行は止めない。
        }
    }

    // ---- センサーイベントの受け取り(エミュレーションスレッドのイベントポンプから呼ぶ) ----
    private struct SensorSample
    {
        public float Gx, Gy, Gz, Ax, Ay, Az;
        public bool HasAccel;
        public double Dt;
    }

    private readonly System.Collections.Generic.List<SensorSample> _sensorSamples = new();
    private bool _sensorEventsSeen, _accelValid;
    private ulong _lastGyroEventTs;
    private float _evAx, _evAy, _evAz;
    private bool _evHasAccel;
    private double _dbgTime;
    private int _dbgSamples;

    /// <summary>SDL_EVENT_GAMEPAD_SENSOR_UPDATEを処理する。1P本体の加速度・ジャイロのサンプルを、ジャイロポインターが有効な間だけ溜める。</summary>
    public void HandleSensorEvent(Sdl.SDL_Event ev)
    {
        if (ev.type != Sdl.SDL_EVENT_GAMEPAD_SENSOR_UPDATE) return;
        if (!GyroPointerEnabled && !_lightgunUsed) { _lastGyroEventTs = 0; return; }

        nint pad = GamepadSlots.GetHandle(0);
        if (pad == nint.Zero || Sdl.SDL_GetGamepadID(pad) != ev.which) return;
        _sensorEventsSeen = true;

        if (ev.sensor == SDL_SENSOR_ACCEL)
        {
            _evAx = ev.sensorX; _evAy = ev.sensorY; _evAz = ev.sensorZ;
            _evHasAccel = true;
            return;
        }
        if (ev.sensor != SDL_SENSOR_GYRO) return;

        ulong ts = ev.sensorTimestampNs;
        double dt = _lastGyroEventTs == 0 || ts <= _lastGyroEventTs ? 0 : (ts - _lastGyroEventTs) / 1e9;
        _lastGyroEventTs = ts;
        if (dt <= 0 || dt > 0.1 || _sensorSamples.Count >= 1024) return;

        _sensorSamples.Add(new SensorSample
        {
            Gx = ev.sensorX, Gy = ev.sensorY, Gz = ev.sensorZ,
            Ax = _evAx, Ay = _evAy, Az = _evAz, HasAccel = _evHasAccel, Dt = dt,
        });
    }

    /// <summary>ジャイロ1サンプル(_gyroBuf/_accelBuf、経過dt秒)を処理して、ポインターの位置を更新する。</summary>
    private void ProcessGyroSample(double dt)
    {
        // ---- ここから JoyShockMapper / GamepadMotionHelpers と同じ考え方の処理 ----
        const double G = 9.80665;
        double wx = _gyroBuf[0] - _gyroBias[0], wy = _gyroBuf[1] - _gyroBias[1], wz = _gyroBuf[2] - _gyroBias[2];
        double angleSpeed = Math.Sqrt(wx * wx + wy * wy + wz * wz);

        // (1) 重力方向(上)の推定=センサーフュージョン。加速度だけだと、振っている最中の加速度が混ざって傾くので、
        //     (a) ジャイロの回転で重力方向を毎フレーム回して追従し、
        //     (b) 加速度の方向へは「揺れている度合い(shakiness)」に応じた速さでゆっくり寄せる(静止時は速く、揺れていると遅く)。
        bool haveAccel = _accelValid;
        double ax = _accelBuf[0] / G, ay = _accelBuf[1] / G, az = _accelBuf[2] / G;
        double an = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (haveAccel && an > 0.3)
        {
            double tx = ax / an, ty = ay / an, tz = az / an;
            if (!_upInit)
            {
                _upVec[0] = tx; _upVec[1] = ty; _upVec[2] = tz;
                _smoothAccel[0] = ax; _smoothAccel[1] = ay; _smoothAccel[2] = az;
                _shakiness = 0;
                _upInit = true;
            }
            else
            {
                RotateByInverse(_upVec, wx, wy, wz, dt);
                RotateByInverse(_smoothAccel, wx, wy, wz, dt);
                double sf = Math.Pow(2.0, -dt / 0.25);
                _shakiness *= sf;
                double sdx = ax - _smoothAccel[0], sdy = ay - _smoothAccel[1], sdz = az - _smoothAccel[2];
                _shakiness = Math.Max(_shakiness, Math.Sqrt(sdx * sdx + sdy * sdy + sdz * sdz));
                _smoothAccel[0] = ax + (_smoothAccel[0] - ax) * sf;
                _smoothAccel[1] = ay + (_smoothAccel[1] - ay) * sf;
                _smoothAccel[2] = az + (_smoothAccel[2] - az) * sf;

                double dx = tx - _upVec[0], dy = ty - _upVec[1], dz = tz - _upVec[2];
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dist > 1e-9)
                {
                    // 既定値は GamepadMotionSettings(still=1.0, shaky=0.1, shakiness 0.01〜0.4, gyro係数0.1 など)
                    double speed = 1.0 + (0.1 - 1.0) * Math.Clamp((_shakiness - 0.01) / (0.4 - 0.01), 0, 1);
                    double gyroLimit = Math.Max(angleSpeed * 0.1, 0.01);
                    if (speed > gyroLimit)
                    {
                        double closeEnough = Math.Clamp((dist - 0.05) / (0.25 - 0.05), 0, 1);
                        speed = gyroLimit + (speed - gyroLimit) * closeEnough;
                    }
                    double step = speed * dt;
                    if (step < dist)
                    {
                        _upVec[0] += dx / dist * step; _upVec[1] += dy / dist * step; _upVec[2] += dz / dist * step;
                    }
                    else
                    {
                        _upVec[0] = tx; _upVec[1] = ty; _upVec[2] = tz;
                    }
                }
            }
        }
        else if (_upInit)
        {
            RotateByInverse(_upVec, wx, wy, wz, dt);
        }
        double un = Math.Sqrt(_upVec[0] * _upVec[0] + _upVec[1] * _upVec[1] + _upVec[2] * _upVec[2]);
        if (un > 1e-6) { _upVec[0] /= un; _upVec[1] /= un; _upVec[2] /= un; }

        // (2) 静止時のずれ(バイアス)の補正。静止(回転が小さく、揺れも小さい)が0.5秒続いたら、
        //     その間の平均へ半減期0.1秒でなじませる。動き始めたら集計をやり直す。
        if (angleSpeed < GyroRestThreshold && _shakiness < 0.08)
        {
            _stillTime += dt;
            for (int i = 0; i < 3; i++) _stillSum[i] += _gyroBuf[i];
            _stillCount++;
            if (_stillTime >= 0.5 && _stillCount > 0)
            {
                double k = 1.0 - Math.Pow(2.0, -dt / 0.1);
                for (int i = 0; i < 3; i++) _gyroBias[i] += (_stillSum[i] / _stillCount - _gyroBias[i]) * k;
                if (_stillCount > 750)
                {
                    for (int i = 0; i < 3; i++) _stillSum[i] *= 0.5;
                    _stillCount /= 2;
                }
            }
        }
        else
        {
            _stillTime = 0;
            _stillCount = 0;
            _stillSum[0] = _stillSum[1] = _stillSum[2] = 0;
        }

        // (3) プレイヤー空間(PLAYER_TURN)の角速度。
        //  - 上下 = 本体のピッチ軸(X)の回転そのまま。
        //  - 左右 = 重力方向まわりの回転。本体のX成分は含めず(含めると上下に動かしたとき左右へ混ざる)、
        //    Y・Z軸の成分だけを重力で射影し、1.41倍に緩めたうえで、Y・Zの回転の大きさを上限にする。
        double pitch = wx;
        double yaw = wy;
        if (_upInit)
        {
            double worldYaw = _upVec[1] * wy + _upVec[2] * wz;
            double yzLen = Math.Sqrt(wy * wy + wz * wz);
            yaw = Math.Sign(worldYaw) * Math.Min(Math.Abs(worldYaw) * 1.41, yzLen);
        }

        // (4) 小さな動きだけ平均化して手ぶれをならす(JoyShockMapperのgyro_smooth: 0.125秒、
        //     しきい値5°/s。しきい値以上の速い動きは遅れなくそのまま使う)。
        {
            double len = Math.Sqrt(yaw * yaw + pitch * pitch);
            double top = GyroSmoothThreshold, bottom = top / 2;
            double immediate = Math.Clamp((len - bottom) / (top - bottom), 0, 1);
            double smooth = 1.0 - immediate;
            _smoothHead = (_smoothHead - 1 + SmoothMax) % SmoothMax;
            _smoothYaw[_smoothHead] = yaw * smooth;
            _smoothPitch[_smoothHead] = pitch * smooth;
            int n = Math.Clamp((int)Math.Round(GyroSmoothTime / dt), 1, SmoothMax);
            double sy = 0, sp = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = (_smoothHead + i) % SmoothMax;
                sy += _smoothYaw[idx];
                sp += _smoothPitch[idx];
            }
            yaw = sy / n + yaw * immediate;
            pitch = sp / n + pitch * immediate;
        }

        // 上下に持ち上げるだけでも、手のぶれで左右に1〜2°/s混ざる(実測)。左右の感度が高いので右へ流れてしまう。
        // 左右(yaw)は「基本の無視量 + 上下の速さに比例した無視量」より小さい分を捨て、超えた分だけを使う。
        // 上下(pitch)は基本のノイズ分だけ捨てる。
        double yawIn = yaw;
        bool gunMode = !LightgunWii && _lightgunUsed;
        double yawDz = (gunMode ? GyroYawDeadzoneGun : GyroYawDeadzone) + GyroYawCrossFactor * Math.Abs(pitch);
        yaw = Math.Abs(yaw) <= yawDz ? 0 : yaw - Math.Sign(yaw) * yawDz;
        pitch = Math.Abs(pitch) <= GyroNoiseDeadzone ? 0 : pitch - Math.Sign(pitch) * GyroNoiseDeadzone;

        double px0 = _gyroPointerX, py0 = _gyroPointerY;
        _gyroPointerX = Math.Clamp(_gyroPointerX + GyroSignY * yaw * (gunMode ? GyroUnitsPerRadXGun : GyroUnitsPerRadX) * dt, -32767.0, 32767.0);
        _gyroPointerY = Math.Clamp(_gyroPointerY + GyroSignX * pitch * GyroUnitsPerRadY * dt, -32767.0, 32767.0);

        // ---- 数値確認用のデバッグ出力(約0.25秒ごと。動きが無い間は約1秒ごと) ----
        const double R2D = 180.0 / Math.PI;
        _dbgYawDeg += yaw * dt * R2D;
        _dbgPitchDeg += pitch * dt * R2D;
        _dbgDx += _gyroPointerX - px0;
        _dbgDy += _gyroPointerY - py0;
        _dbgTime += dt;
        _dbgSamples++;
        bool moved = Math.Abs(_dbgYawDeg) > 0.3 || Math.Abs(_dbgPitchDeg) > 0.3;
        if (DebugLogEnabled && _dbgTime >= (moved ? 0.25 : 1.0))
        {
            double un2 = Math.Sqrt(_upVec[0] * _upVec[0] + _upVec[1] * _upVec[1] + _upVec[2] * _upVec[2]);
            double tilt = un2 > 0.1 ? Math.Asin(Math.Clamp(-_upVec[2] / un2, -1, 1)) * R2D : 0; // 前方(-Z)が水平から上を向く角度の目安
            System.Diagnostics.Debug.WriteLine(
                $"[Gyro] raw(°/s) x={_gyroBuf[0] * R2D,6:F1} y={_gyroBuf[1] * R2D,6:F1} z={_gyroBuf[2] * R2D,6:F1}"
                + $" | bias(°/s) {_gyroBias[0] * R2D:F2},{_gyroBias[1] * R2D:F2},{_gyroBias[2] * R2D:F2}"
                + $" | up=({_upVec[0]:F2},{_upVec[1]:F2},{_upVec[2]:F2}) tilt={tilt:F0}° shaky={_shakiness:F2} 静止={_stillTime:F1}s サンプル={_dbgSamples}回/{(_sensorEventsSeen ? "イベント" : "ポーリング")}"
                + $" | yaw={yawIn * R2D,6:F1}→{yaw * R2D,5:F1}°/s pitch={pitch * R2D,6:F1}°/s"
                + $" | 区間回転 yaw={_dbgYawDeg,5:F1}° pitch={_dbgPitchDeg,5:F1}°"
                + $" | 区間移動 X={_dbgDx / 32767.0 * 100,5:F1}% Y={_dbgDy / 32767.0 * 100,5:F1}%"
                + $" | 位置 X={_gyroPointerX / 32767.0 * 100,5:F1}% Y={_gyroPointerY / 32767.0 * 100,5:F1}%"
                + $" | 無視: 縦={GyroNoiseDeadzone * R2D:F2}°/s 横={GyroYawDeadzone * R2D:F1}°/s+縦速度×{GyroYawCrossFactor:F2} 静止判定={GyroRestThreshold * R2D:F1}°/s"
                + $" | 横{GyroUnitsPerRadX / 32767.0 * 100 * Math.PI / 180:F1}%/° 縦{GyroUnitsPerRadY / 32767.0 * 100 * Math.PI / 180:F1}%/°");
            _dbgYawDeg = _dbgPitchDeg = _dbgDx = _dbgDy = 0;
            _dbgTime = 0;
            _dbgSamples = 0;
        }
    }

    /// <summary>
    /// true(既定)はWii(Dolphin)のライトガン: 照準はジャイロ、ボタンはJOYPADで読むコア向け。
    /// false はドリームキャスト/NAOMI(Flycast)のライトガン: 照準は <see cref="LightgunAimMode"/> で選び、
    /// ボタンもライトガンのIDで返す(PollLightgunAim/GetLightgunState)。GamePlayPageが設定する。
    /// </summary>
    public volatile bool LightgunWii = true;

    /// <summary>Wii以外のライトガンの照準方法。0=右スティックで動かす 1=右スティックの位置へ直接 2=ジャイロ(マウス/タッチはどれでも併用)。</summary>
    public volatile int LightgunAimMode;

    // ライトガンのボタンID(libretro.h)。
    private const uint LIGHTGUN_ID_TRIGGER = 2;
    private const uint LIGHTGUN_ID_AUX_A = 3;
    private const uint LIGHTGUN_ID_AUX_B = 4;
    private const uint LIGHTGUN_ID_START = 6;
    private const uint LIGHTGUN_ID_SELECT = 7;
    private const uint LIGHTGUN_ID_AUX_C = 8;
    private const uint LIGHTGUN_ID_DPAD_UP = 9;
    private const uint LIGHTGUN_ID_DPAD_DOWN = 10;
    private const uint LIGHTGUN_ID_DPAD_LEFT = 11;
    private const uint LIGHTGUN_ID_DPAD_RIGHT = 12;
    private const uint LIGHTGUN_ID_RELOAD = 16;

    // 右スティックで照準を動かす速さ(画面の横いっぱい=65534を、最大の傾きで約1秒)。傾きの2乗で、少し傾けたときは細かく動く。
    private const double AimStickUnitsPerSec = 65534.0;
    private const double AimStickDeadzone = 0.2;
    private long _aimLastTicks;
    private double _aim1X, _aim1Y;       // 2P(ポート1)の照準位置(右スティックのみ)
    private bool _aimStickWas0, _aimStickWas1;

    /// <summary>
    /// ライトガン(Wii以外)の照準を、右スティックとマウス(タッチ)、ジャイロで更新する。毎フレームのpoll内から呼ぶ。
    /// 1Pの位置は1つの変数を共有し、マウスが動いたときはその絶対位置へ、右スティックを倒している間はスティックで動かす
    /// (最後に動かしたほうが優先)。2Pは右スティックのみ。エミュレーションスレッドからのみ呼ぶ。
    /// </summary>
    public void PollLightgunAim()
    {
        if (!_lightgunUsed || LightgunWii) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _aimLastTicks == 0 ? 0 : (now - _aimLastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _aimLastTicks = now;
        if (dt <= 0 || dt > 0.1) dt = 0;

        int mode = LightgunAimMode;

        // 1P マウス/タッチ: カーソルが動いたら、その位置(画面の絶対位置)へ。
        if (_mouseInside && (_mouseDx != 0 || _mouseDy != 0))
        {
            _gyroPointerX = _pointerX;
            _gyroPointerY = _pointerY;
            _aimStickWas0 = false;
        }

        // 1P 右スティック(ジャイロ照準のときは使わない)
        if (mode != 2) UpdateAimFromStick(_snaps[0], mode, dt, ref _gyroPointerX, ref _gyroPointerY, ref _aimStickWas0);

        // 2P 右スティック
        UpdateAimFromStick(_snaps[1], mode == 1 ? 1 : 0, dt, ref _aim1X, ref _aim1Y, ref _aimStickWas1);
    }

    private static void UpdateAimFromStick(PadSnapshot pad, int mode, double dt, ref double x, ref double y, ref bool was)
    {
        if (!pad.Connected) return;
        // 左右どちらのスティックでも動かせる(大きく倒しているほうを使う)
        double rx = StickAxis(pad.RightX), ry = StickAxis(pad.RightY), lx = StickAxis(pad.LeftX), ly = StickAxis(pad.LeftY);
        bool useL = lx * lx + ly * ly > rx * rx + ry * ry;
        double sx = useL ? lx : rx, sy = useL ? ly : ry;
        bool active = sx != 0 || sy != 0;
        if (mode == 1)
        {
            // 倒した位置へ直接。離すと中央へ戻る(スティックで動かしていたときだけ)。
            if (active) { x = sx * 32767.0; y = sy * 32767.0; was = true; }
            else if (was) { x = 0; y = 0; was = false; }
            return;
        }

        if (active && dt > 0)
        {
            x = Math.Clamp(x + sx * Math.Abs(sx) * AimStickUnitsPerSec * dt, -32767.0, 32767.0);
            y = Math.Clamp(y + sy * Math.Abs(sy) * AimStickUnitsPerSec * dt, -32767.0, 32767.0);
        }
    }

    /// <summary>スティックの軸(-32768〜32767)を、デッドゾーンを除いた -1〜1 へ。</summary>
    private static double StickAxis(short v)
    {
        double n = v / 32767.0;
        double a = Math.Abs(n);
        if (a < AimStickDeadzone) return 0;
        return Math.Sign(n) * Math.Min(1.0, (a - AimStickDeadzone) / (1.0 - AimStickDeadzone));
    }

    /// <summary>RETRO_DEVICE_LIGHTGUNの問い合わせ。位置(SCREEN_X/Y)は、ジャイロ(Wii)または右スティック/マウス/ジャイロ(それ以外)から作った値。</summary>
    private short GetLightgunState(uint port, uint id)
    {
        _lightgunUsed = true;
        _pointerDeviceUsed = true; // マウス位置の読み取りを有効にする
        if (LightgunWii)
        {
            if (port != 0) return 0;
            return id switch
            {
                LIGHTGUN_ID_SCREEN_X => (short)_gyroPointerX,
                LIGHTGUN_ID_SCREEN_Y => (short)_gyroPointerY,
                LIGHTGUN_ID_IS_OFFSCREEN => 0, // 位置は常に画面内に収める
                _ => 0,
            };
        }

        // ボタンは、コントローラー設定(割り当て)を通したJOYPADの状態から作る。
        // 撃つ=✕/R2/マウス左、リロード=○/L2/マウス右(画面の外を撃つ動作)、補助=□/△。マウスは1Pのみ。
        bool Joy(uint joyId) => InputState(port, RetroDevice.JOYPAD, 0, joyId) != 0;
        bool mouseL = port == 0 && _mouseInside && _mouseLeft;
        bool mouseR = port == 0 && _mouseInside && _mouseRight;
        bool reload = Joy(RetroJoypad.A) || Joy(RetroJoypad.L2) || mouseR;
        double ax = port == 0 ? _gyroPointerX : _aim1X;
        double ay = port == 0 ? _gyroPointerY : _aim1Y;
        return id switch
        {
            LIGHTGUN_ID_SCREEN_X => (short)ax,
            LIGHTGUN_ID_SCREEN_Y => (short)ay,
            LIGHTGUN_ID_IS_OFFSCREEN => (short)(reload ? 1 : 0),
            LIGHTGUN_ID_TRIGGER => (short)(Joy(RetroJoypad.B) || Joy(RetroJoypad.R2) || mouseL ? 1 : 0),
            LIGHTGUN_ID_RELOAD => (short)(reload ? 1 : 0),
            LIGHTGUN_ID_AUX_A => (short)(Joy(RetroJoypad.Y) ? 1 : 0),
            LIGHTGUN_ID_AUX_B => (short)(Joy(RetroJoypad.X) ? 1 : 0),
            LIGHTGUN_ID_AUX_C => 0,
            LIGHTGUN_ID_START => (short)(Joy(RetroJoypad.START) ? 1 : 0),
            LIGHTGUN_ID_SELECT => (short)(Joy(RetroJoypad.SELECT) ? 1 : 0),
            LIGHTGUN_ID_DPAD_UP => (short)(Joy(RetroJoypad.UP) ? 1 : 0),
            LIGHTGUN_ID_DPAD_DOWN => (short)(Joy(RetroJoypad.DOWN) ? 1 : 0),
            LIGHTGUN_ID_DPAD_LEFT => (short)(Joy(RetroJoypad.LEFT) ? 1 : 0),
            LIGHTGUN_ID_DPAD_RIGHT => (short)(Joy(RetroJoypad.RIGHT) ? 1 : 0),
            _ => 0,
        };
    }

    /// <summary>「ポインターを中央へ」の組み合わせの押し始め(エッジ)で、中央戻しを要求する。毎フレームのpoll内から呼ぶ。</summary>
    public void DetectPointerRecenterCombo()
    {
        bool comboNow = IsComboDown(Hotkeys.PointerRecenter);
        _pointerRecenterHeld = comboNow;
        if (comboNow && !_pointerRecenterPrevPressed) _pointerRecenterRequested = true;
        _pointerRecenterPrevPressed = comboNow;
    }

    /// <summary>
    /// 各枠のパッド状態を読み取って保存する(retro_input_poll_t内から、SDLイベントのポンプ後に毎フレーム呼ぶ)。
    /// </summary>
    public void CapturePads()
    {
        for (int slot = 0; slot < _snaps.Length; slot++)
        {
            _snaps[slot] = PadSnapshot.Capture(GamepadSlots.GetHandle(slot));
        }
    }

    public void RefreshKeyboardState()
    {
        nint keyPtr = Sdl.SDL_GetKeyboardState(out int numKeys);
        if (keyPtr == nint.Zero) return;

        if (_keyState.Length != numKeys)
            _keyState = new byte[numKeys];
        Marshal.Copy(keyPtr, _keyState, 0, numKeys);
    }

    /// <summary>
    /// ゲーム実行中(ポーズしていない間)、ZL+ZR(SDL上はL2/R2、アナログトリガー軸)の
    /// 同時押しを検出する。「タップ」(長押し閾値に達する前に離す)なら中断メニューを、
    /// 「ホールド」(閾値以上押し続ける)なら巻き戻し画面を開く要求を発行する。
    /// 毎フレーム呼ばれるretro_input_poll_t内で行うことで、レトロパッド入力の
    /// 読み取りと同じタイミング・同じスレッドで完結させる。
    /// </summary>
    /// <param name="isHwRendered">
    /// HWレンダリング中は巻き戻しキャプチャ自体を無効化しているため、長押しを検知済み扱いに
    /// するだけで巻き戻し画面は開かない(離したときに通常の中断メニューが開くのも防ぐ)。
    /// </param>
    public void DetectMenuToggleCombo(bool isHwRendered)
    {
        bool touchpadNow = IsComboDown(TouchpadCombo);
        if (touchpadNow && !_touchpadPrev) SystemPanelRequested?.Invoke(this, EventArgs.Empty);
        _touchpadPrev = touchpadNow;

        GamepadButton[] menuCombo = Hotkeys.Menu;
        GamepadButton[] rewindCombo = Hotkeys.Rewind;

        // 中断メニューと同じ組み合わせを、長押しの操作(巻き戻し、無ければ早送り)にも割り当てているとき:
        // タップなら中断メニュー、長押しならその操作。
        bool longIsRewind = SameCombo(menuCombo, rewindCombo);
        bool longIsFastForward = !longIsRewind && menuCombo.Length > 0 && SameCombo(menuCombo, Hotkeys.FastForward);

        if (!longIsRewind && !longIsFastForward)
        {
            // 中断メニューと巻き戻しが別々の組み合わせのとき: それぞれ押した瞬間に発火する。
            bool menuNow = IsComboDown(menuCombo);
            bool rewindNow = IsComboDown(rewindCombo);

            if (menuNow && !_menuTogglePrevCombo)
            {
                MenuToggleRequested?.Invoke(this, EventArgs.Empty);
            }

            if (rewindNow && !_rewindPrevCombo && !isHwRendered)
            {
                RewindToggleRequested?.Invoke(this, EventArgs.Empty);
            }

            _menuTogglePrevCombo = menuNow;
            _rewindPrevCombo = rewindNow;
            _menuComboLongPressFired = false;
            return;
        }

        // 同じ組み合わせのとき(既定): タップなら中断メニュー、長押しなら巻き戻し。
        bool comboNow = IsComboDown(menuCombo);
        var now = DateTime.UtcNow;

        if (comboNow && !_menuTogglePrevCombo)
        {
            // 押し始めた瞬間: まだタップかホールドか分からないので何もせず、
            // 判定用の基準時刻だけ記録しておく。
            _menuComboPressStart = now;
            _menuComboLongPressFired = false;
        }
        else if (comboNow && _menuTogglePrevCombo)
        {
            // 押し続けている間: 長押し閾値を超えた瞬間に一度だけ巻き戻し画面を開く。
            if (!_menuComboLongPressFired && now - _menuComboPressStart >= MenuComboLongPressThreshold)
            {
                _menuComboLongPressFired = true;
                if (longIsFastForward)
                {
                    FastForwardComboPressed?.Invoke(this, EventArgs.Empty);
                }
                else if (!isHwRendered)
                {
                    RewindToggleRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        else if (!comboNow && _menuTogglePrevCombo)
        {
            // 離した瞬間: 巻き戻し(長押し)としてすでに処理済みでなければ、
            // 通常の中断メニュー開閉(タップ)として扱う。
            if (!_menuComboLongPressFired)
            {
                MenuToggleRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        _menuTogglePrevCombo = comboNow;
        _rewindPrevCombo = false;
    }

    /// <summary>
    /// ゲーム実行中(ポーズしていない間)、早送り(速く)の組み合わせの押し始めを検出する。
    /// 押し始めた瞬間(エッジ)に1回だけ発火し、押しっぱなしの間は再発火しない。
    /// 毎フレーム呼ばれるretro_input_poll_t内から呼ぶ。
    /// </summary>
    public void DetectFastForwardCombo()
    {
        // 中断メニューと同じ組み合わせなら、早送りは「長押し」として中断メニュー側(DetectMenuToggleCombo)で扱う。
        if (Hotkeys.FastForward.Length > 0 && SameCombo(Hotkeys.FastForward, Hotkeys.Menu)
            && !SameCombo(Hotkeys.Menu, Hotkeys.Rewind))
        {
            _fastForwardComboPrevPressed = false;
            return;
        }

        bool comboNow = IsComboDown(Hotkeys.FastForward);

        // 早送りのボタンが、中断メニュー/巻き戻しのより大きい組み合わせに含まれる場合(R2 と L2+R2)は、
        // L2+R2を押すときに早送りが誤発動しないよう、押した時点では発火せず「離したとき」に発火する。
        // 途中でその大きい組み合わせが押されたら、早送りは取り消す。
        if (IsSubsetOfLargerHotkey(Hotkeys.FastForward))
        {
            if (comboNow && !_fastForwardComboPrevPressed) _fastForwardPending = true;
            if (_fastForwardPending && (IsComboDown(Hotkeys.Menu) || IsComboDown(Hotkeys.Rewind))) _fastForwardPending = false;
            if (!comboNow && _fastForwardComboPrevPressed && _fastForwardPending)
            {
                _fastForwardPending = false;
                FastForwardComboPressed?.Invoke(this, EventArgs.Empty);
            }
            _fastForwardComboPrevPressed = comboNow;
            return;
        }

        if (comboNow && !_fastForwardComboPrevPressed)
        {
            FastForwardComboPressed?.Invoke(this, EventArgs.Empty);
        }

        _fastForwardComboPrevPressed = comboNow;
    }

    /// <summary>combo が、中断メニューまたは巻き戻しの(より大きい)組み合わせにすべて含まれているか。</summary>
    private bool IsSubsetOfLargerHotkey(GamepadButton[] combo)
    {
        if (combo.Length == 0) return false;
        foreach (GamepadButton[] other in new[] { Hotkeys.Menu, Hotkeys.Rewind })
        {
            if (other.Length <= combo.Length) continue;
            bool all = true;
            foreach (GamepadButton b in combo)
            {
                if (Array.IndexOf(other, b) < 0) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    /// <summary>早送り(遅く=逆方向)の組み合わせの押し始めを検出する(DetectFastForwardComboの逆方向版)。</summary>
    public void DetectFastForwardReverseCombo()
    {
        bool comboNow = IsComboDown(Hotkeys.FastForwardReverse);

        if (comboNow && !_fastForwardReverseComboPrevPressed)
        {
            FastForwardReverseComboPressed?.Invoke(this, EventArgs.Empty);
        }

        _fastForwardReverseComboPrevPressed = comboNow;
    }

    /// <summary>
    /// 組み合わせの全ボタンが、1P・2Pのどちらか1台のコントローラーで同時に押されているか。
    /// 空の組み合わせ(無効)は常にfalse。
    /// </summary>
    private bool IsComboDown(GamepadButton[] combo)
    {
        if (combo.Length == 0) return false;

        for (int slot = 0; slot < _snaps.Length; slot++)
        {
            PadSnapshot snap = _snaps[slot];
            if (!snap.Connected) continue;

            bool all = true;
            foreach (GamepadButton button in combo)
            {
                if (!GamepadPhysicalButtonReader.IsPressed(snap, button))
                {
                    all = false;
                    break;
                }
            }

            if (all) return true;
        }

        return false;
    }

    /// <summary>いま押されているホットキーの組み合わせに含まれる物理ボタン(コアへ渡さない)。</summary>
    private volatile GamepadButton[] _hotkeySuppressed = Array.Empty<GamepadButton>();

    /// <summary>
    /// ホットキー(中断メニュー/巻き戻し/早送り等)の組み合わせが押されている間、その組み合わせの
    /// ボタンをコアへ渡さないようにする(早送りのR+ZRがゲームのR/R2として効いてしまうのを防ぐ)。
    /// 1ボタンのホットキー(例: 右スティック押し込み=中断メニュー)はそのボタンが常にコアへ渡らなくなる。
    /// 毎フレーム、各Detect*Comboの後に呼ぶ。
    /// </summary>
    public void UpdateHotkeySuppression()
    {
        List<GamepadButton>? list = null;
        foreach (GamepadButton[] combo in new[] { Hotkeys.Menu, Hotkeys.Rewind, Hotkeys.FastForward, Hotkeys.FastForwardReverse, Hotkeys.PointerRecenter })
        {
            if (!IsComboDown(combo)) continue;
            list ??= new List<GamepadButton>();
            foreach (GamepadButton button in combo)
            {
                if (!list.Contains(button)) list.Add(button);
            }
        }

        _hotkeySuppressed = list is null ? Array.Empty<GamepadButton>() : list.ToArray();
    }

    /// <summary>このRetroPadのキーに割り当てた物理ボタン(または連射の物理ボタン)が、ホットキーとして使用中か。</summary>
    private bool IsHotkeySuppressedKey(string retroPadKey)
    {
        GamepadButton[] suppressed = _hotkeySuppressed;
        if (suppressed.Length == 0) return false;

        if (_resolvedMapping.TryGetValue(retroPadKey, out GamepadButton? mapped))
        {
            if (mapped is GamepadButton m && Array.IndexOf(suppressed, m) >= 0) return true;
        }
        else if (Array.IndexOf(suppressed, ControllerMappingService.GetBuiltinPhysicalButton(retroPadKey)) >= 0)
        {
            return true;
        }

        return _turboResolved.TryGetValue(retroPadKey, out GamepadButton turbo) && Array.IndexOf(suppressed, turbo) >= 0;
    }

    private static bool SameCombo(GamepadButton[] a, GamepadButton[] b)
    {
        if (a.Length != b.Length) return false;
        foreach (GamepadButton button in a)
        {
            if (Array.IndexOf(b, button) < 0) return false;
        }

        return true;
    }

    private bool IsKeyDown(int scancode) =>
        scancode >= 0 && scancode < _keyState.Length && _keyState[scancode] != 0;

    /// <summary>
    /// trueなら、キーボードをコアへRETRO_DEVICE_KEYBOARDとして渡す(パソコン系のコア向け)。
    /// このとき、キーボードをパッドの代わりに使う処理(矢印キー=十字キー等)は止める
    /// (キーを打つたびにパッドのボタンも押されるのを防ぐ)。パッドは今までどおりJOYPADで渡す。
    /// </summary>
    public bool KeyboardPassthrough { get; set; }

    private bool _keyboardMenuPrev;
    private bool _keyboardPanelPrev;
    private bool _keyboardFastForwardPrev;

    // ---- 中断メニュー(一時停止中)のキーボード操作: 矢印キー=移動、Enter=決定、Esc/BackSpace=もどる ----

    // Vk = Windowsの仮想キーコード。一時停止中はゲームのSDLウィンドウが隠れていて、他のウィンドウへ移って戻った後など
    // SDLにキーボードフォーカスが無いことがある。そのときも効くよう、自分のアプリが前面なら GetAsyncKeyState でも調べる。
    private static readonly (int Scancode, int Vk, GamepadButton Button, bool Repeat)[] PausedMenuKeys =
    {
        (Sdl.SDL_SCANCODE_UP, 0x26, GamepadButton.DPadUp, true),
        (Sdl.SDL_SCANCODE_DOWN, 0x28, GamepadButton.DPadDown, true),
        (Sdl.SDL_SCANCODE_LEFT, 0x25, GamepadButton.DPadLeft, true),
        (Sdl.SDL_SCANCODE_RIGHT, 0x27, GamepadButton.DPadRight, true),
        (Sdl.SDL_SCANCODE_RETURN, 0x0D, GamepadButton.A, false),   // テンキーのEnterも同じ仮想キー
        (88, 0, GamepadButton.A, false),   // テンキーのEnter(SDL側のみ。仮想キー0x0Dは上のEnterと同じなので、ここでは調べない=二重に発火させない)
        (41, 0x1B, GamepadButton.B, false),   // Esc
        (42, 0x08, GamepadButton.B, false),   // BackSpace
    };

    private static bool IsOwnProcessForeground()
    {
        nint foreground = Win32.GetForegroundWindow();
        if (foreground == nint.Zero) return false;
        Win32.GetWindowThreadProcessId(foreground, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private readonly Dictionary<int, (DateTime Start, DateTime Last)> _pausedKeysHeld = new();
    private bool _pausedKeyboardPrimed;

    /// <summary>
    /// 一時停止中に呼ぶ。キーボードの操作を、メニュー操作のボタン(A=決定、B=もどる)として<paramref name="fire"/>へ渡す。
    /// 一時停止に入った時点で押されていたキーは、いったん離されるまで押下として扱わない
    /// (ゲーム中に押していたEnterが、そのままメニューの決定になるのを防ぐ)。矢印キーは押し続けると繰り返す。
    /// </summary>
    public void PollPausedKeyboard(DateTime now, Action<GamepadButton> fire)
    {
        RefreshKeyboardState();
        bool ownForeground = IsOwnProcessForeground();
        foreach ((int scancode, int vk, GamepadButton button, bool repeat) in PausedMenuKeys)
        {
            bool down = IsKeyDown(scancode) || (ownForeground && vk != 0 && (Win32.GetAsyncKeyState(vk) & 0x8000) != 0);
            bool held = _pausedKeysHeld.TryGetValue(scancode, out (DateTime Start, DateTime Last) state);

            if (!down)
            {
                if (held) _pausedKeysHeld.Remove(scancode);
                continue;
            }

            if (!_pausedKeyboardPrimed)
            {
                _pausedKeysHeld[scancode] = (now, now);
                continue;
            }

            if (!held)
            {
                _pausedKeysHeld[scancode] = (now, now);
                fire(button);
            }
            else if (repeat && now - state.Start >= TimeSpan.FromMilliseconds(400)
                     && now - state.Last >= TimeSpan.FromMilliseconds(140))
            {
                _pausedKeysHeld[scancode] = (state.Start, now);
                fire(button);
            }
        }

        _pausedKeyboardPrimed = true;
    }

    // コアがSET_KEYBOARD_CALLBACKで登録したキー通知。flycast等は、InputStateで問い合わせず、こちらだけでキーを受け取る。
    private RetroKeyboardEventCallback? _keyboardCallback;
    private readonly HashSet<int> _keyboardDown = new();

    /// <summary>コアが登録したキー通知の関数ポインタを受け取る(null/0で解除)。</summary>
    public void SetKeyboardCallback(nint function)
    {
        _keyboardCallback = function == nint.Zero
            ? null
            : Marshal.GetDelegateForFunctionPointer<RetroKeyboardEventCallback>(function);
        _keyboardDown.Clear();
    }

    /// <summary>
    /// キーボード直通のとき、前回からキーの押し/離しが変わったものを、コアのキー通知へ送る
    /// (RetroArchと同じく、毎フレームの入力取得のタイミングで呼ぶ)。直通でなくなったら、押したままのキーを離した扱いにする。
    /// </summary>
    public void DispatchKeyboardEvents()
    {
        RetroKeyboardEventCallback? callback = _keyboardCallback;
        if (callback is null) return;

        if (!KeyboardPassthrough)
        {
            if (_keyboardDown.Count == 0) return;
            foreach (KeyValuePair<int, uint> kv in KeyboardMap.EventKeys)
            {
                if (_keyboardDown.Remove(kv.Key)) callback(false, kv.Value, 0, 0);
            }
            _keyboardDown.Clear();
            return;
        }

        bool shift = IsKeyDown(225) || IsKeyDown(229);
        ushort mods = 0;
        if (shift) mods |= 0x01;
        if (IsKeyDown(224) || IsKeyDown(228)) mods |= 0x02;
        if (IsKeyDown(226) || IsKeyDown(230)) mods |= 0x04;
        if (IsKeyDown(227) || IsKeyDown(231)) mods |= 0x08;

        foreach (KeyValuePair<int, uint> kv in KeyboardMap.EventKeys)
        {
            bool down = IsKeyDown(kv.Key);
            bool was = _keyboardDown.Contains(kv.Key);
            if (down == was) continue;

            if (down) _keyboardDown.Add(kv.Key); else _keyboardDown.Remove(kv.Key);
            callback(down, kv.Value, down ? KeyboardMap.CharacterOf(kv.Value, shift) : 0, mods);
        }
    }

    /// <summary>
    /// ランチャーのキー操作(F1=システム設定パネル、F11=早送りの倍率切り替え、F12=中断メニュー)。
    /// キーボード直通のON/OFFに関わらず、どのコンソールでも有効(パッドが無くても開けるように)。
    /// 押した瞬間に1回だけ発火する。これらのキーはコアへは渡さない(<see cref="KeyboardMap"/>)。
    /// </summary>
    public void DetectKeyboardMenuKey()
    {
        // ゲームが動いている間は、一時停止中のキー状態をリセットしておく(次に止まったときの「押しっぱなし」判定のため)。
        _pausedKeyboardPrimed = false;
        _pausedKeysHeld.Clear();

        bool now = IsKeyDown(KeyboardMap.MenuScancode);
        if (now && !_keyboardMenuPrev) MenuToggleRequested?.Invoke(this, EventArgs.Empty);
        _keyboardMenuPrev = now;

        // F11: 早送りの倍率切り替え(パッドのR+ZRと同じ。コアには渡さない)。
        bool ff = IsKeyDown(KeyboardMap.FastForwardScancode);
        if (ff && !_keyboardFastForwardPrev) FastForwardComboPressed?.Invoke(this, EventArgs.Empty);
        _keyboardFastForwardPrev = ff;

        // F1: システム設定パネル(パッドのタッチパッドと同じ。コアには渡さない)。
        bool panel = IsKeyDown(KeyboardMap.SystemPanelScancode);
        if (panel && !_keyboardPanelPrev) SystemPanelRequested?.Invoke(this, EventArgs.Empty);
        _keyboardPanelPrev = panel;
    }

    /// <summary>
    /// 左スティックが指定した軸方向へデッドゾーンを超えて倒れているかどうか。
    /// JOYPADの方向ボタン(UP/DOWN/LEFT/RIGHT)へのフォールバック用。
    /// (RETRO_DEVICE_JOYPADしか見ない2Dコアでも、スティック入力で方向操作できるようにするため)
    /// </summary>
    private static bool IsStickDirection(short value, bool positive)
    {
        return positive ? value > AnalogStickDeadzone : value < -AnalogStickDeadzone;
    }

    /// <summary>
    /// RetroPadのボタン(retroPadKey。"A"等)が現在押されているかどうかを、ButtonMappingで
    /// 指定された物理ボタン経由で判定する。ButtonMappingにキーが無い/値が不正な場合は
    /// builtinFallback(コントローラー設定画面を導入する前の既定挙動と同じ物理ボタン)を使う。
    /// </summary>
    private bool IsRetroPadButtonPressed(in PadSnapshot pad, string retroPadKey, GamepadButton builtinFallback)
    {
        // 値がnullの場合は"None"=「このボタンはコアへ送らない」(cores/libretro.jsonのinput_remap用)。
        if (_resolvedMapping.TryGetValue(retroPadKey, out GamepadButton? mapped))
        {
            return mapped is GamepadButton m && GamepadPhysicalButtonReader.IsPressed(pad, m);
        }

        return GamepadPhysicalButtonReader.IsPressed(pad, builtinFallback);
    }

    /// <summary>
    /// 方向入力のキー("Up"、"LSUp"等)に割り当てられた物理入力を返す。割り当てなし("None")ならfalse。
    /// 割り当てが無ければ既定(同名の物理入力)。
    /// </summary>
    private bool TryGetDirectionPhysical(string key, out GamepadButton physical)
    {
        if (_resolvedMapping.TryGetValue(key, out GamepadButton? mapped) && mapped is GamepadButton m)
        {
            physical = m;
            return true;
        }

        physical = default;
        return false;
    }

    /// <summary>方向入力のキーが「押されている」か(JOYPADの十字キー用)。</summary>
    private bool IsDirectionPressed(in PadSnapshot pad, string key)
        => TryGetDirectionPhysical(key, out GamepadButton physical) && GamepadPhysicalButtonReader.IsPressed(pad, physical);

    /// <summary>方向入力のキーの強さ(0〜32767)。スティックの傾きはその軸の生の値、ボタン類は押していれば最大。</summary>
    private int DirectionMagnitude(in PadSnapshot pad, string key)
    {
        if (!TryGetDirectionPhysical(key, out GamepadButton physical)) return 0;

        switch (physical)
        {
            case GamepadButton.LeftStickUp: return Math.Max(0, -(int)pad.LeftY);
            case GamepadButton.LeftStickDown: return Math.Max(0, (int)pad.LeftY);
            case GamepadButton.LeftStickLeft: return Math.Max(0, -(int)pad.LeftX);
            case GamepadButton.LeftStickRight: return Math.Max(0, (int)pad.LeftX);
            case GamepadButton.RightStickUp: return Math.Max(0, -(int)pad.RightY);
            case GamepadButton.RightStickDown: return Math.Max(0, (int)pad.RightY);
            case GamepadButton.RightStickLeft: return Math.Max(0, -(int)pad.RightX);
            case GamepadButton.RightStickRight: return Math.Max(0, (int)pad.RightX);
            default: return GamepadPhysicalButtonReader.IsPressed(pad, physical) ? short.MaxValue : 0;
        }
    }

    private short GetAnalogStickValue(in PadSnapshot pad, uint index, uint id)
    {
        if (!pad.Connected) return 0;

        // 方向の割り当てが変えられているスティックだけ、割り当てに従って値を組み立てる
        // (変えていなければ従来どおり生の値を返す)。
        if (id <= RETRO_DEVICE_ID_ANALOG_Y && index <= RETRO_DEVICE_INDEX_ANALOG_RIGHT)
        {
            string p = index == RETRO_DEVICE_INDEX_ANALOG_LEFT ? "LS" : "RS";
            if (index == RETRO_DEVICE_INDEX_ANALOG_LEFT ? _leftStickRemapped : _rightStickRemapped)
            {
                int value = id == RETRO_DEVICE_ID_ANALOG_X
                    ? DirectionMagnitude(pad, p + "Right") - DirectionMagnitude(pad, p + "Left")
                    : DirectionMagnitude(pad, p + "Down") - DirectionMagnitude(pad, p + "Up");
                return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            }
        }

        return (index, id) switch
        {
            (RETRO_DEVICE_INDEX_ANALOG_LEFT, RETRO_DEVICE_ID_ANALOG_X) => pad.LeftX,
            (RETRO_DEVICE_INDEX_ANALOG_LEFT, RETRO_DEVICE_ID_ANALOG_Y) => pad.LeftY,
            (RETRO_DEVICE_INDEX_ANALOG_RIGHT, RETRO_DEVICE_ID_ANALOG_X) => pad.RightX,
            (RETRO_DEVICE_INDEX_ANALOG_RIGHT, RETRO_DEVICE_ID_ANALOG_Y) => pad.RightY,
            _ => 0,
        };
    }

    /// <summary>true にすると、ジャイロの数値([Gyro])と、コアのPOINTER/MOUSE/LIGHTGUN問い合わせ([InputQuery])をデバッグ出力する。通常は無効。</summary>
    public static bool DebugLogEnabled { get; set; } = false;

    private readonly System.Collections.Generic.HashSet<ulong> _loggedQueries = new();

    /// <summary>retro_input_state_t本体。EmulationHostが RetroDevice.ANALOG/JOYPAD の振り分けをせず、そのままここへ渡す。</summary>
    public short InputState(uint port, uint device, uint index, uint id)
    {
        // 1P=ポート0、2P=ポート1。それ以外のポートは常に未入力。
        if (port >= GamepadSlots.MaxSlots)
            return 0;

        PadSnapshot pad = _snaps[port];

        // N64コア(mupen64plus-next等)は、ポートのデバイス種別がJOYPADのままでも
        // device=RETRO_DEVICE_ANALOG で直接スティックの値を問い合わせてくる
        // (SetControllerPortDeviceの設定とは独立した経路のため、JOYPADと
        //  同じswitch文の中ではなく別処理として扱う)。
        if (device == RetroDevice.ANALOG)
        {
            if (port < _coreQueriesAnalog.Length) _coreQueriesAnalog[port] = true;
            if (IgnoreRightStickAnalog && index == 1) return 0; // Wii「マウス+ジャイロ」: 右スティックが傾き入力になりジャイロと二重になるのを防ぐ
            return GetAnalogStickValue(pad, index, id);
        }

        // 診断: JOYPAD/ANALOG以外(POINTER/MOUSE/LIGHTGUN等)の問い合わせを、種類ごとに1回だけログへ出す。
        if (DebugLogEnabled && device != RetroDevice.JOYPAD && _loggedQueries.Add(((ulong)port << 48) | ((ulong)device << 32) | ((ulong)index << 16) | id))
            System.Diagnostics.Debug.WriteLine($"[InputQuery] port={port} device={device} index={index} id={id} (mouseInside={_mouseInside} lightgunUsed={_lightgunUsed})");

        // Wiiのジャイロポインター(IRポインタ)。ポート0の位置を返す。
        if (device == RetroDevice.LIGHTGUN)
            return GetLightgunState(port, id);

        // DS/3DS等の下画面タッチ。ポート0のカーソル位置/ボタンを返す。
        if (device == RetroDevice.POINTER || device == RetroDevice.MOUSE)
            return GetMouseState(port, device, index, id);

        // キーボード直通: コアが問い合わせたキー(RETROK_*)が押されているか。
        if (device == RetroDevice.KEYBOARD)
            return KeyboardPassthrough && IsKeyDown(KeyboardMap.ToScancode(id)) ? (short)1 : (short)0;

        if (device != RetroDevice.JOYPAD)
            return 0;

        // RETRO_DEVICE_ID_JOYPAD_MASK: 全ボタンの状態をビットマスク(bit N = RETRO_DEVICE_ID_JOYPAD_N)で
        // 一括して返す問い合わせ。RetroArchはGET_INPUT_BITMASKS環境コマンドに応答した
        // コアに対してこれを提供しており、LRPS2(PS2)のようにこの経路"だけ"でボタンを
        // 読むコアでは、ここが0のままだと一切のボタン入力が効かなくなる。
        if (id == RETRO_DEVICE_ID_JOYPAD_MASK)
        {
            int mask = 0;
            for (uint bit = 0; bit < 16; bit++)
            {
                if (InputState(port, device, index, bit) != 0) mask |= 1 << (int)bit;
            }
            return unchecked((short)mask);
        }

        bool pressed = false;

        if (pad.Connected)
        {
            // SDL3のSOUTH/EAST/WEST/NORTHは物理位置ベースなので、
            // Xbox/Switch/PS5(DualSense)いずれのパッドでも同じコードで正しく動く。
            // 方向キーは D-pad に加えて、左スティックの傾きもデジタル方向として拾う
            // (2DコアなどJOYPADの方向ボタンしか見ないコアのためのフォールバック)。
            // スティックを別途ANALOGで読むコアでは、スティックを十字キーとして重ねない。
            bool stickAsDpad = StickAlwaysAsDpad || !(port < _coreQueriesAnalog.Length && _coreQueriesAnalog[port]);
            pressed = id switch
            {
                // 十字キー/左スティックの割り当て(コントローラー設定)に従う。既定は十字キーと左スティックそのまま。
                RetroJoypad.UP => IsDirectionPressed(pad, "Up") || (stickAsDpad && IsDirectionPressed(pad, "LSUp")),
                RetroJoypad.DOWN => IsDirectionPressed(pad, "Down") || (stickAsDpad && IsDirectionPressed(pad, "LSDown")),
                RetroJoypad.LEFT => IsDirectionPressed(pad, "Left") || (stickAsDpad && IsDirectionPressed(pad, "LSLeft")),
                RetroJoypad.RIGHT => IsDirectionPressed(pad, "Right") || (stickAsDpad && IsDirectionPressed(pad, "LSRight")),
                // Nintendo配列(右がA/下がB)に合わせてEAST/SOUTHを入れ替えている
                // (PS5なら Cross=SOUTH→B、Circle=EAST→A に相当)のが元々の既定挙動。
                // コントローラー設定画面(デフォルト設定/コア毎の設定)で変更されていれば、
                // ButtonMappingで指定された物理ボタンを見る(IsRetroPadButtonPressed参照)。
                RetroJoypad.A => IsRetroPadButtonPressed(pad, "A", GamepadButton.B),
                RetroJoypad.B => IsRetroPadButtonPressed(pad, "B", GamepadButton.A),
                RetroJoypad.X => IsRetroPadButtonPressed(pad, "X", GamepadButton.Y),
                RetroJoypad.Y => IsRetroPadButtonPressed(pad, "Y", GamepadButton.X),
                RetroJoypad.START => IsRetroPadButtonPressed(pad, "Start", GamepadButton.Menu),
                RetroJoypad.SELECT => IsRetroPadButtonPressed(pad, "Select", GamepadButton.View),
                RetroJoypad.L => IsRetroPadButtonPressed(pad, "L", GamepadButton.LeftShoulder),
                RetroJoypad.R => IsRetroPadButtonPressed(pad, "R", GamepadButton.RightShoulder),
                // L2/R2はアナログトリガー、L3/R3はスティック押し込み(PS2/PS1/N64系コア等が使う)。
                // 既定はトリガー軸/スティック押し込みそのまま。cores/libretro.jsonのinput_remap等で
                // "L2"/"R2"/"L3"/"R3"が指定されていれば、そのマッピングを優先する
                // (例: melonDSのR2=画面入れ替えを右スティック押し込みへ移す)。
                RetroJoypad.L2 => ButtonMapping.ContainsKey("L2")
                    ? IsRetroPadButtonPressed(pad, "L2", GamepadButton.LeftTrigger)
                    : pad.LeftTriggerValue > TriggerPressThreshold,
                RetroJoypad.R2 => ButtonMapping.ContainsKey("R2")
                    ? IsRetroPadButtonPressed(pad, "R2", GamepadButton.RightTrigger)
                    : pad.RightTriggerValue > TriggerPressThreshold,
                RetroJoypad.L3 => ButtonMapping.ContainsKey("L3")
                    ? IsRetroPadButtonPressed(pad, "L3", GamepadButton.LeftThumbstickButton)
                    : pad.IsDown(PadButton.LeftStick),
                RetroJoypad.R3 => ButtonMapping.ContainsKey("R3")
                    ? IsRetroPadButtonPressed(pad, "R3", GamepadButton.RightThumbstickButton)
                    : pad.IsDown(PadButton.RightStick),
                _ => false,
            };
        }

        // キーボードは1P専用(2Pには割り当てない)。キーボード直通のときは、パッドの代わりには使わない。
        if (!pressed && port == 0 && !KeyboardPassthrough)
        {
            pressed = id switch
            {
                RetroJoypad.UP => IsKeyDown(Sdl.SDL_SCANCODE_UP),
                RetroJoypad.DOWN => IsKeyDown(Sdl.SDL_SCANCODE_DOWN),
                RetroJoypad.LEFT => IsKeyDown(Sdl.SDL_SCANCODE_LEFT),
                RetroJoypad.RIGHT => IsKeyDown(Sdl.SDL_SCANCODE_RIGHT),
                RetroJoypad.A => IsKeyDown(Sdl.SDL_SCANCODE_X),
                RetroJoypad.B => IsKeyDown(Sdl.SDL_SCANCODE_Z),
                RetroJoypad.X => IsKeyDown(Sdl.SDL_SCANCODE_S),
                RetroJoypad.Y => IsKeyDown(Sdl.SDL_SCANCODE_A),
                RetroJoypad.START => IsKeyDown(Sdl.SDL_SCANCODE_RETURN),
                RetroJoypad.SELECT => IsKeyDown(Sdl.SDL_SCANCODE_RSHIFT),
                RetroJoypad.L => IsKeyDown(Sdl.SDL_SCANCODE_Q),
                RetroJoypad.R => IsKeyDown(Sdl.SDL_SCANCODE_W),
                _ => false,
            };
        }

        // 連射: 連射に割り当てた物理ボタンを押している間、そのキーを「押す/離す」の繰り返しで返す。
        if (!pressed && pad.Connected && _turboResolved.Count > 0)
        {
            string? turboKey = RetroPadKeyForId(id);
            if (turboKey is not null
                && _turboResolved.TryGetValue(turboKey, out GamepadButton turboPhysical)
                && GamepadPhysicalButtonReader.IsPressed(pad, turboPhysical)
                && IsTurboPhaseOn())
            {
                pressed = true;
            }
        }

        // ホットキーとして押されているボタンはコアへ渡さない。
        if (pressed && id < 16 && RetroPadKeyForId(id) is string hotkeyKey && IsHotkeySuppressedKey(hotkeyKey))
            pressed = false;

        // 存在しないボタンはコアへ渡さない(許可リスト外の物理入力は無効)。
        if (id < 16 && (AllowedButtonMask & (1u << (int)id)) == 0)
            pressed = false;

        // ホスト側の仮想押下(ディスク入れ替えの代行など)。連射の対象外。
        if (!pressed && port == 0 && id < 16 && (VirtualPressMask & (1u << (int)id)) != 0)
            return 1;

        return (short)(pressed ? 1 : 0);
    }

    /// <summary>RetroJoypadのidに対応するRetroPadのキー名("A"等)。対応しなければnull。</summary>
    private static string? RetroPadKeyForId(uint id) => id switch
    {
        RetroJoypad.A => "A",
        RetroJoypad.B => "B",
        RetroJoypad.X => "X",
        RetroJoypad.Y => "Y",
        RetroJoypad.L => "L",
        RetroJoypad.R => "R",
        RetroJoypad.L2 => "L2",
        RetroJoypad.R2 => "R2",
        RetroJoypad.L3 => "L3",
        RetroJoypad.R3 => "R3",
        RetroJoypad.START => "Start",
        RetroJoypad.SELECT => "Select",
        _ => null,
    };

    /// <summary>連射の「押している」側の時間帯か(1周期=1/TurboRateHz秒の前半が押す、後半が離す)。</summary>
    private static bool IsTurboPhaseOn()
    {
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        long halfPeriods = ticks * (TurboRateHz * 2) / System.Diagnostics.Stopwatch.Frequency;
        return (halfPeriods & 1) == 0;
    }
}