using System;

namespace EmulatorLauncher.Input;

/// <summary>
/// SDL3のゲームコントローラAPI(SDL_Gamepad)をポーリングし、押下/長押し/十字キーの
/// オートリピートをイベントとして発行するサービス。
/// コンソール選択・ゲーム選択・(ゲーム未実行時の)中断メニューなど、
/// メニュー画面のナビゲーション全般で使われる唯一のゲームパッド入力経路であり、
/// ゲーム実行中に使われる EmulationHost 側の入力処理とは独立している。
///
/// SDLイベントポンプについて:
/// SDL_PumpEvents/SDL_PollEvent は同時に複数スレッドから呼び出してはならない。
/// このサービスは「ゲームが実行されていない間」だけ動作する前提であり、
/// GamePlayPage が EmulationHost を起動する直前に <see cref="Stop"/> を呼び、
/// エミュレーション終了後(EmulationHost.Stop()の完了後)に <see cref="Start"/> を
/// 呼び直すことで、イベントポンプの所有権をEmulationHostの専用スレッドと
/// 排他的に切り替える(MainWindow / GamePlayPage 側の責務)。
///
/// ゲームパッドサブシステム自体の初期化・終了は <see cref="SdlGamepadInit"/> が
/// アプリ全体で一度だけ行うため、このクラスでは行わない。
/// </summary>
public sealed class SdlGamepadService
{
    private readonly IUiPollingTimer _timer;
    // メニュー操作は1P・2Pのどちらのコントローラーでも行える(枠ごとに押下の追跡を持ち、
    // 発行するイベントは共通)。割り当ての管理(2台まで・切断時の枠の扱い)はGamepadSlotsが行う。
    private readonly GamepadEdgeTracker[] _trackers = new GamepadEdgeTracker[GamepadSlots.MaxSlots];
    private readonly nint[] _lastPads = new nint[GamepadSlots.MaxSlots];

    // 枠のコントローラーが入れ替わった(接続/切断/1Pと2Pの入れ替え)直後は、そのコントローラーで押されている
    // ボタンが全部離されるまで、押下として扱わない(入れ替えを決定したAボタンが、入れ替え後の
    // 枠で「新しい押下」になって、もう一度入れ替わってしまうのを防ぐ)。
    private readonly bool[] _awaitSlotRelease = new bool[GamepadSlots.MaxSlots];

    // 外部コマンド実行中の「コンボ長押し監視モード」用の状態(BeginComboWatch参照)。
    private Action? _comboCallback;
    private GamepadButton _comboA;
    private GamepadButton _comboB;
    private TimeSpan _comboHold;
    private DateTime? _comboStart;
    private bool _comboFired;

    // コンボ監視終了直後、押されたままのボタンがメニュー操作として誤発火しないよう、
    // 全ボタンが一度離されるまで通常のエッジ検出を保留するフラグ。
    private bool _awaitAllReleased;

    /// <summary>ボタン押下の立ち上がり、または十字キー長押し中のオートリピート。</summary>
    public event Action<GamepadButton>? ButtonPressed;

    /// <summary>ボタンが長押し閾値を超えたときに一度だけ発火。</summary>
    public event Action<GamepadButton>? ButtonLongPressed;

    /// <summary>十字キーを押し続けた際のオートリピート専用イベント。</summary>
    public event Action<GamepadButton>? ButtonRepeated;

    /// <summary>ボタンが離されたとき。</summary>
    public event Action<GamepadButton>? ButtonReleased;

    /// <summary>
    /// コンボ監視中、両ボタンが押されている間の進捗(0.0〜1.0)。離されたら 0 が通知される。
    /// UIスレッドから発火する。
    /// </summary>
    public event Action<double>? ComboHoldProgress;

    /// <summary>メニュー操作に使えるコントローラー(1P/2Pのどちらか)が接続されているか。</summary>
    public bool IsConnected => GamepadSlots.ConnectedCount > 0;

    public SdlGamepadService(IUiPollingTimer timer)
    {
        for (int i = 0; i < _trackers.Length; i++)
        {
            var tracker = new GamepadEdgeTracker();
            tracker.ButtonPressed += b => ButtonPressed?.Invoke(b);
            tracker.ButtonLongPressed += b => ButtonLongPressed?.Invoke(b);
            tracker.ButtonRepeated += b => ButtonRepeated?.Invoke(b);
            tracker.ButtonReleased += b => ButtonReleased?.Invoke(b);
            _trackers[i] = tracker;
        }

        _timer = timer;
        _timer.Configure(TimeSpan.FromMilliseconds(40), Poll); // 約25Hz
    }

    /// <summary>
    /// イベントポンプを開始する。開始直後、すでに接続されているコントローラも
    /// SDL_EVENT_GAMEPAD_ADDED として検出できるよう、まず一度イベントキューを
    /// 空にしてから通常のポーリングへ入る。
    /// </summary>
    public void Start()
    {
        System.Diagnostics.Debug.WriteLine($"[SdlGamepadService] Start: connected={GamepadSlots.ConnectedCount}");
        Sdl.SDL_PumpEvents();
        while (Sdl.SDL_PollEvent(out var initialEv))
        {
            HandleDeviceEvent(initialEv);
        }

        // アプリ起動直後の最初のStart()では、SDL_INIT_GAMEPAD直後にキューへ積まれた
        // SDL_EVENT_GAMEPAD_ADDEDを上のポンプで拾える。それ以外のタイミングでStart()が
        // 呼ばれた場合(念のための保険)にも既存の接続を確実に検出できるよう、
        // 取りこぼした切断の整理と、一度だけの能動的な列挙も行っておく。
        GamepadSlots.Reconcile();
        GamepadSlots.EnsureInitialEnumeration();

        ResetTrackers();

        // 再開した時点で押されたままのボタン(ゲーム中のメニューで「タイトルにもどる」を決定した
        // Aボタンなど)を、再開後の画面(ゲーム選択画面など)への新しい押下として拾わないよう、
        // 全ボタンが一度離されるまで通常のエッジ検出を保留する。
        _awaitAllReleased = true;

        _timer.Start();
        System.Diagnostics.Debug.WriteLine($"[SdlGamepadService] Start完了: connected={GamepadSlots.ConnectedCount}");
    }

    /// <summary>
    /// イベントポンプを停止する。EmulationHost がイベントキューの所有権を
    /// 引き継ぐ直前(ゲーム開始前)に必ず呼び出すこと。
    /// </summary>
    public void Stop()
    {
        System.Diagnostics.Debug.WriteLine("[SdlGamepadService] Stop");
        _timer.Stop();
    }

    /// <summary>
    /// 外部コマンド実行中用のモードへ切り替える。このモードの間は通常のメニュー操作イベント
    /// (ButtonPressed/LongPressed/Repeated)を一切発行せず、<paramref name="a"/> と
    /// <paramref name="b"/> の同時押しが <paramref name="hold"/> 続いたときに
    /// <paramref name="onHeld"/> を一度だけ呼ぶ(UIスレッド)。
    /// ポーリングタイマー自体は動かし続けること(Stopしない)。
    /// </summary>
    public void BeginComboWatch(GamepadButton a, GamepadButton b, TimeSpan hold, Action onHeld)
    {
        _comboA = a;
        _comboB = b;
        _comboHold = hold;
        _comboStart = null;
        _comboFired = false;
        _comboCallback = onHeld;
        ResetTrackers();
    }

    /// <summary>
    /// コンボ監視を終了して通常のメニュー操作へ戻す。押しっぱなしのボタンが
    /// 誤ってメニュー操作になるのを避けるため、全ボタンが離されるまで通常イベントは保留される。
    /// </summary>
    public void EndComboWatch()
    {
        if (_comboCallback is null) return;

        _comboCallback = null;
        _comboStart = null;
        _comboFired = false;
        _awaitAllReleased = true;
        ComboHoldProgress?.Invoke(0);
    }

    private void Poll()
    {
        Sdl.SDL_PumpEvents();
        while (Sdl.SDL_PollEvent(out var ev))
        {
            HandleDeviceEvent(ev);
        }

        var now = DateTime.UtcNow;

        if (_comboCallback is not null)
        {
            PollCombo(now);
            return;
        }

        if (_awaitAllReleased)
        {
            if (AnyButtonDown()) return;
            _awaitAllReleased = false;
            ResetTrackers();
        }

        for (int slot = 0; slot < _trackers.Length; slot++)
        {
            // その枠のコントローラーが入れ替わった(切断/再接続)ときは、押下状態の追跡をやり直す。
            nint pad = GamepadSlots.GetHandle(slot);
            if (pad != _lastPads[slot])
            {
                _lastPads[slot] = pad;
                _trackers[slot].Reset();
                _awaitSlotRelease[slot] = pad != nint.Zero;
            }

            if (_awaitSlotRelease[slot])
            {
                if (AnyButtonDown(pad)) continue;
                _awaitSlotRelease[slot] = false;
                _trackers[slot].Reset();
            }

            _trackers[slot].Poll(pad, now);
        }
    }

    private void ResetTrackers()
    {
        for (int slot = 0; slot < _trackers.Length; slot++)
        {
            _lastPads[slot] = GamepadSlots.GetHandle(slot);
            _trackers[slot].Reset();
        }
    }

    private void PollCombo(DateTime now)
    {
        bool down = false;
        for (int slot = 0; slot < GamepadSlots.MaxSlots && !down; slot++)
        {
            nint pad = GamepadSlots.GetHandle(slot);
            down = pad != nint.Zero
                   && GamepadPhysicalButtonReader.IsPressed(pad, _comboA)
                   && GamepadPhysicalButtonReader.IsPressed(pad, _comboB);
        }

        if (!down)
        {
            if (_comboStart is not null) ComboHoldProgress?.Invoke(0);
            _comboStart = null;
            _comboFired = false;
            return;
        }

        _comboStart ??= now;
        double progress = Math.Min(1.0, (now - _comboStart.Value).TotalMilliseconds / _comboHold.TotalMilliseconds);
        ComboHoldProgress?.Invoke(progress);

        if (progress >= 1.0 && !_comboFired)
        {
            _comboFired = true;
            _comboCallback?.Invoke();
        }
    }

    private bool AnyButtonDown()
    {
        for (int slot = 0; slot < GamepadSlots.MaxSlots; slot++)
        {
            if (AnyButtonDown(GamepadSlots.GetHandle(slot))) return true;
        }

        return false;
    }

    private static bool AnyButtonDown(nint pad)
    {
        return PadSnapshot.Capture(pad).AnyDown;
    }

    /// <summary>接続/切断イベントはGamepadSlotsへ渡す(割り当ては1P/2Pの2枠で管理される)。</summary>
    private void HandleDeviceEvent(Sdl.SDL_Event ev)
    {
        string? message = GamepadSlots.HandleDeviceEvent(ev);
        if (message is not null)
        {
            System.Diagnostics.Debug.WriteLine($"[SdlGamepadService] {message}");
        }
    }
}
