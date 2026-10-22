
namespace EmulatorLauncher.Input;

/// <summary>SDLゲームパッドハンドルから、論理ボタン()の現在の押下状態を読み取る共通ヘルパー。</summary>
public static class GamepadPhysicalButtonReader
{
    // L2/R2を「押した」とみなす標準のしきい値(PadSnapshotと共通)。
    public const short TriggerThreshold = PadSnapshot.TriggerThreshold;

    /// <summary>指定した論理ボタンが現在押されているかどうか。</summary>
    public static bool IsPressed(nint gamepad, GamepadButton button)
        => IsPressed(PadSnapshot.Capture(gamepad), button);

    /// <summary>読み取り済みの状態()から、指定した論理ボタンが押されているかどうか。</summary>
    public static bool IsPressed(in PadSnapshot snapshot, GamepadButton button)
    {
        // スティックの傾き(デッドゾーンを超えたら押した扱い。SDLのY軸は下が正)。
        switch (button)
        {
            case GamepadButton.LeftStickUp: return snapshot.Connected && snapshot.LeftY < -StickThreshold;
            case GamepadButton.LeftStickDown: return snapshot.Connected && snapshot.LeftY > StickThreshold;
            case GamepadButton.LeftStickLeft: return snapshot.Connected && snapshot.LeftX < -StickThreshold;
            case GamepadButton.LeftStickRight: return snapshot.Connected && snapshot.LeftX > StickThreshold;
            case GamepadButton.RightStickUp: return snapshot.Connected && snapshot.RightY < -StickThreshold;
            case GamepadButton.RightStickDown: return snapshot.Connected && snapshot.RightY > StickThreshold;
            case GamepadButton.RightStickLeft: return snapshot.Connected && snapshot.RightX < -StickThreshold;
            case GamepadButton.RightStickRight: return snapshot.Connected && snapshot.RightX > StickThreshold;
        }

        return snapshot.IsDown(PadButtons.FromGamepadButton(button));
    }

    /// <summary>スティックを「その方向へ倒した」とみなすしきい値(ゲームの十字キー代わりに使う値と同じ)。</summary>
    public const short StickThreshold = 8000;

    /// <summary>保存された物理ボタン名(GamepadButtonの名前の文字列。例: "LeftShoulder")をGamepadButtonへ変換する。</summary>
    public static GamepadButton ParseOrDefault(string? name, GamepadButton fallback)
        => !string.IsNullOrEmpty(name) && Enum.TryParse(name, out GamepadButton parsed) ? parsed : fallback;
}
