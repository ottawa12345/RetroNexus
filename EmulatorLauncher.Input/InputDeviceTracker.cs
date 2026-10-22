namespace EmulatorLauncher.Input;

/// <summary>ランチャーの操作に最後に使われた入力機器の種類。</summary>
public enum LastInputDevice
{
    Gamepad,
    Keyboard,
    Mouse,
    Touch,
}

/// <summary>
/// 最後に使われた入力機器(ゲームパッド/キーボード/マウス/タッチ)を覚えておく。
/// 操作ガイド(HintBar)のキー表示の切り替えや、マウスカーソルの自動非表示に使う。
/// UIスレッドからだけ呼ぶこと。
/// </summary>
public static class InputDeviceTracker
{
    public static LastInputDevice Current { get; private set; } = LastInputDevice.Gamepad;

    /// <summary>最後に使われた機器が変わったときに発生する。</summary>
    public static event Action? Changed;

    /// <summary>操作ガイドをキーボードのキー名で表示するか(最後の操作がキーボードのとき)。</summary>
    public static bool UsesKeyboardLabels => Current == LastInputDevice.Keyboard;

    public static void Report(LastInputDevice device)
    {
        if (Current == device) return;
        Current = device;
        Changed?.Invoke();
    }
}
