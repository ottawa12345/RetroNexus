using System;
using System.Collections.Generic;

namespace EmulatorLauncher.Input;

/// <summary>
/// SDL3のゲームパッドハンドル(nint)から現在のボタン状態を読み取り、
/// 「押下の立ち上がり」「長押し」「(十字キー等の)オートリピート」を
/// エッジイベントとして発行する、状態を持った小さなヘルパー。
///
/// このロジックは元々 GamepadService(Windows.Gaming.Input版)にしか無かったが、
/// アプリ全体をSDL3ゲームコントローラへ統一するにあたり、
///   ・メニュー画面(コンソール選択/ゲーム選択)を操作する <see cref="SdlGamepadService"/>
///   ・ゲーム一時停止中の中断メニューを操作する EmulationHost 内のポーリング
/// の両方から同じアルゴリズムを再利用できるよう、独立したクラスに切り出してある。
/// どちらの利用者も「SDLのゲームパッドハンドルを定期的にこのクラスへ渡す」だけでよく、
/// SDLイベントポンプ自体は呼び出し側(唯一のポンプ所有者)が行う。
/// </summary>
public sealed class GamepadEdgeTracker
{
    private static readonly TimeSpan LongPressThreshold = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(140);

    private static readonly HashSet<GamepadButton> RepeatableButtons = new()
    {
        GamepadButton.DPadUp, GamepadButton.DPadDown, GamepadButton.DPadLeft, GamepadButton.DPadRight,
    };

    private readonly Dictionary<GamepadButton, bool> _previousState = new();
    private readonly Dictionary<GamepadButton, DateTime> _pressStart = new();
    private readonly HashSet<GamepadButton> _longPressFired = new();
    private readonly Dictionary<GamepadButton, DateTime> _lastRepeat = new();

    /// <summary>ボタン押下の立ち上がり、または十字キー長押し中のオートリピート。</summary>
    public event Action<GamepadButton>? ButtonPressed;

    /// <summary>ボタンが長押し閾値を超えたときに一度だけ発火。</summary>
    public event Action<GamepadButton>? ButtonLongPressed;

    /// <summary>十字キーを押し続けた際のオートリピート専用イベント(ButtonPressedとは別に監視したい場合用)。</summary>
    public event Action<GamepadButton>? ButtonRepeated;

    /// <summary>ボタンが離されたときに発火。「長押しで別の操作、短押しは離した瞬間に実行」にするボタンの判定に使う。</summary>
    public event Action<GamepadButton>? ButtonReleased;

    /// <summary>
    /// 指定したSDLゲームパッドハンドルの現在状態を読み取り、必要なイベントを発行する。
    /// <paramref name="gamepad"/> が <see cref="nint.Zero"/>(未接続)の場合は、
    /// すべてのボタンが離された状態として扱い、内部状態をリセットする。
    /// 呼び出し前に SDL_PumpEvents / SDL_PollEvent を呼ぶのは呼び出し側の責務。
    /// </summary>
    public void Poll(nint gamepad, DateTime now) => Poll(PadSnapshot.Capture(gamepad), now);

    /// <summary>
    /// 読み取り済みの状態(<see cref="PadSnapshot"/>)から、必要なイベントを発行する。
    /// 未接続(Connected=false)の場合は、すべてのボタンが離された状態として内部状態をリセットする。
    /// ボタンの名前は、ランチャーのメニュー操作用(右のボタン=A/決定、下のボタン=B/もどる)に変換する。
    /// L2/R2はアナログ軸なので、<see cref="PadSnapshot.TriggerThreshold"/>でデジタル化して同じロジックに乗せる。
    /// </summary>
    public void Poll(in PadSnapshot snapshot, DateTime now)
    {
        if (!snapshot.Connected)
        {
            _previousState.Clear();
            _pressStart.Clear();
            _longPressFired.Clear();
            _lastRepeat.Clear();
            return;
        }

        foreach (PadButton padButton in PadButtons.All)
        {
            GamepadButton button = PadButtons.ToLauncherButton(padButton);
            bool wasDown = _previousState.TryGetValue(button, out var prev) && prev;
            // 左スティックも十字キーと同じ方向の操作として扱う(メニュー操作用)。
            bool isDown = snapshot.IsDown(padButton) || IsStickDirection(snapshot, button, wasDown);
            HandleButtonState(button, isDown, wasDown, now);
            _previousState[button] = isDown;
        }
    }

    // 左スティックを方向入力として扱うしきい値(32767中)。倒し始めは大きめ、戻りは小さめにして、境目でのばたつきを防ぐ。
    private const int StickPressThreshold = 18000;
    private const int StickReleaseThreshold = 10000;

    /// <summary>
    /// 左スティックが指定の十字キー方向に倒されているか。斜めのときは、より大きく倒した軸の方向だけを採る
    /// (上と右が同時に入って、意図しない移動にならないように)。
    /// </summary>
    private static bool IsStickDirection(in PadSnapshot snapshot, GamepadButton button, bool wasDown)
    {
        if (!snapshot.Connected) return false;
        int x = snapshot.LeftX, y = snapshot.LeftY;   // SDLのY軸は下が正
        int threshold = wasDown ? StickReleaseThreshold : StickPressThreshold;
        bool vertical = Math.Abs(y) >= Math.Abs(x);

        return button switch
        {
            GamepadButton.DPadUp => vertical && -y >= threshold,
            GamepadButton.DPadDown => vertical && y >= threshold,
            GamepadButton.DPadLeft => !vertical && -x >= threshold,
            GamepadButton.DPadRight => !vertical && x >= threshold,
            _ => false,
        };
    }

    /// <summary>内部状態(押下開始時刻・長押し発火済みフラグ等)をすべてリセットする。</summary>
    public void Reset()
    {
        _previousState.Clear();
        _pressStart.Clear();
        _longPressFired.Clear();
        _lastRepeat.Clear();
    }

    private void HandleButtonState(GamepadButton button, bool isDown, bool wasDown, DateTime now)
    {
        if (isDown && !wasDown)
        {
            _pressStart[button] = now;
            _longPressFired.Remove(button);
            _lastRepeat[button] = now;
            ButtonPressed?.Invoke(button);
        }
        else if (isDown && wasDown)
        {
            if (_pressStart.TryGetValue(button, out var start))
            {
                var held = now - start;

                if (held >= LongPressThreshold && _longPressFired.Add(button))
                {
                    ButtonLongPressed?.Invoke(button);
                }

                if (RepeatableButtons.Contains(button)
                    && held >= RepeatDelay
                    && _lastRepeat.TryGetValue(button, out var last)
                    && now - last >= RepeatInterval)
                {
                    _lastRepeat[button] = now;
                    ButtonRepeated?.Invoke(button);
                }
            }
        }
        else if (!isDown && wasDown)
        {
            ButtonReleased?.Invoke(button);
            _pressStart.Remove(button);
            _longPressFired.Remove(button);
            _lastRepeat.Remove(button);
        }
    }
}
