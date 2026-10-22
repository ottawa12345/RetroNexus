using System;

namespace EmulatorLauncher.Input;

/// <summary>
/// ゲームパッドの物理ボタン(位置ベースの名前)。SDL3の SDL_GamepadButton と同じ考え方で、
/// メーカーやラベルに関わらず「位置」で呼ぶ。
///
///   South = 下のボタン(PS: ×、Xbox: A、Switch: B)
///   East  = 右のボタン(PS: ○、Xbox: B、Switch: A)
///   West  = 左のボタン(PS: □、Xbox: X、Switch: Y)
///   North = 上のボタン(PS: △、Xbox: Y、Switch: X)
///
/// アプリ内部でSDLのボタン番号との対応を持つのは <see cref="PadButtons"/> だけ。
/// ランチャーのメニュー操作(<see cref="GamepadButton"/>、East=A/決定)や、保存済みの
/// 割り当て設定(GamepadButtonの名前、South=A)との変換も <see cref="PadButtons"/> に集約している。
/// </summary>
public enum PadButton
{
    South,
    East,
    West,
    North,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftShoulder,
    RightShoulder,
    Start,
    Back,
    LeftStick,
    RightStick,
    /// <summary>L2(アナログ軸。<see cref="PadSnapshot.TriggerThreshold"/> を超えたら押下)。</summary>
    LeftTrigger,
    /// <summary>R2(アナログ軸。<see cref="PadSnapshot.TriggerThreshold"/> を超えたら押下)。</summary>
    RightTrigger,
    /// <summary>PS4/PS5のタッチパッド押し込み(末尾に追加。ビット位置を変えないため)。</summary>
    Touchpad,
}

/// <summary>
/// <see cref="PadButton"/> とSDLのボタン番号、および既存の <see cref="GamepadButton"/> との対応表。
/// SDL→ボタンの対応は、ここだけに持つ(以前は3か所に重複していた)。
/// </summary>
public static class PadButtons
{
    /// <summary>全ボタン(列挙順)。</summary>
    public static readonly PadButton[] All = Enum.GetValues<PadButton>();

    /// <summary>SDLのデジタルボタン番号。トリガー(アナログ軸)は -1。</summary>
    public static int SdlButtonOf(PadButton button) => button switch
    {
        PadButton.South => Sdl.SDL_GAMEPAD_BUTTON_SOUTH,
        PadButton.East => Sdl.SDL_GAMEPAD_BUTTON_EAST,
        PadButton.West => Sdl.SDL_GAMEPAD_BUTTON_WEST,
        PadButton.North => Sdl.SDL_GAMEPAD_BUTTON_NORTH,
        PadButton.DPadUp => Sdl.SDL_GAMEPAD_BUTTON_DPAD_UP,
        PadButton.DPadDown => Sdl.SDL_GAMEPAD_BUTTON_DPAD_DOWN,
        PadButton.DPadLeft => Sdl.SDL_GAMEPAD_BUTTON_DPAD_LEFT,
        PadButton.DPadRight => Sdl.SDL_GAMEPAD_BUTTON_DPAD_RIGHT,
        PadButton.LeftShoulder => Sdl.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER,
        PadButton.RightShoulder => Sdl.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER,
        PadButton.Start => Sdl.SDL_GAMEPAD_BUTTON_START,
        PadButton.Back => Sdl.SDL_GAMEPAD_BUTTON_BACK,
        PadButton.LeftStick => Sdl.SDL_GAMEPAD_BUTTON_LEFT_STICK,
        PadButton.RightStick => Sdl.SDL_GAMEPAD_BUTTON_RIGHT_STICK,
        PadButton.Touchpad => Sdl.SDL_GAMEPAD_BUTTON_TOUCHPAD,
        _ => -1,
    };

    /// <summary>
    /// 保存済みの割り当て設定・ホットキー(<see cref="GamepadButton"/>の名前)が指す物理ボタン。
    /// この「設定側」の名前では A=South(下)、B=East(右)(ランチャーのメニュー操作とは逆)。
    /// 既存の設定ファイルの値を変えずに読めるよう、この対応をそのまま維持している。
    /// </summary>
    public static PadButton FromGamepadButton(GamepadButton button) => button switch
    {
        GamepadButton.A => PadButton.South,
        GamepadButton.B => PadButton.East,
        GamepadButton.X => PadButton.West,
        GamepadButton.Y => PadButton.North,
        GamepadButton.DPadUp => PadButton.DPadUp,
        GamepadButton.DPadDown => PadButton.DPadDown,
        GamepadButton.DPadLeft => PadButton.DPadLeft,
        GamepadButton.DPadRight => PadButton.DPadRight,
        GamepadButton.LeftShoulder => PadButton.LeftShoulder,
        GamepadButton.RightShoulder => PadButton.RightShoulder,
        GamepadButton.LeftTrigger => PadButton.LeftTrigger,
        GamepadButton.RightTrigger => PadButton.RightTrigger,
        GamepadButton.Menu => PadButton.Start,
        GamepadButton.View => PadButton.Back,
        GamepadButton.LeftThumbstickButton => PadButton.LeftStick,
        GamepadButton.RightThumbstickButton => PadButton.RightStick,
        GamepadButton.Touchpad => PadButton.Touchpad,
        _ => PadButton.South,
    };

    /// <summary>
    /// ランチャーのメニュー操作(<see cref="IControllerPage"/>に渡すボタン)での名前。
    /// Switchと同じく、右のボタン(East)=A(決定)、下のボタン(South)=B(もどる)。
    /// 「操作ボタンの入れ替え」は、この後段(MainWindowのTranslateForLauncher)で行う。
    /// </summary>
    public static GamepadButton ToLauncherButton(PadButton button) => button switch
    {
        PadButton.East => GamepadButton.A,
        PadButton.South => GamepadButton.B,
        PadButton.West => GamepadButton.X,
        PadButton.North => GamepadButton.Y,
        PadButton.DPadUp => GamepadButton.DPadUp,
        PadButton.DPadDown => GamepadButton.DPadDown,
        PadButton.DPadLeft => GamepadButton.DPadLeft,
        PadButton.DPadRight => GamepadButton.DPadRight,
        PadButton.LeftShoulder => GamepadButton.LeftShoulder,
        PadButton.RightShoulder => GamepadButton.RightShoulder,
        PadButton.LeftTrigger => GamepadButton.LeftTrigger,
        PadButton.RightTrigger => GamepadButton.RightTrigger,
        PadButton.Start => GamepadButton.Menu,
        PadButton.Back => GamepadButton.View,
        PadButton.LeftStick => GamepadButton.LeftThumbstickButton,
        PadButton.RightStick => GamepadButton.RightThumbstickButton,
        PadButton.Touchpad => GamepadButton.Touchpad,
        _ => GamepadButton.A,
    };
}
