namespace EmulatorLauncher.Input;

/// <summary>
/// アプリ全体(コンソール選択・ゲーム選択・ゲームプレイ・中断メニュー)で共通して使う
/// 論理ゲームパッドボタン。実体は <see cref="SdlGamepadService"/> または
/// <c>EmulatorLauncher.Emulation.EmulationHost</c> が SDL3 のゲームコントローラAPI
/// (SDL_Gamepad)から読み取ったボタン状態をこのenumへ変換して発行する。
///
/// SDL3の SDL_GamepadButton は物理位置ではなく「Xboxパッド基準の論理配置」で
/// 名前が付けられており(SOUTH/EAST/WEST/NORTH)、メーカーやレイアウトに関わらず
/// 同じAPIで統一的に扱える。DualSense(PS5)を例にすると以下のように対応する。
///
///   物理ボタン(DualSense)      SDL_GamepadButton                   論理ボタン(このenum)
///   ------------------------  -----------------------------------  --------------------
///   × (クロス)                 SDL_GAMEPAD_BUTTON_SOUTH             B(もどる)
///   ○ (サークル)               SDL_GAMEPAD_BUTTON_EAST              A(決定。Switchと同じ「右のボタン=決定」)
///   □ (スクエア)               SDL_GAMEPAD_BUTTON_WEST              X
///   △ (トライアングル)         SDL_GAMEPAD_BUTTON_NORTH             Y
///   十字キー 上/下/左/右       SDL_GAMEPAD_BUTTON_DPAD_*            DPadUp / DPadDown / DPadLeft / DPadRight
///   L1                         SDL_GAMEPAD_BUTTON_LEFT_SHOULDER     LeftShoulder
///   R1                         SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER    RightShoulder
///   L2 (アナログ軸)             SDL_GAMEPAD_AXIS_LEFT_TRIGGER        LeftTrigger  (閾値でデジタル化)
///   R2 (アナログ軸)             SDL_GAMEPAD_AXIS_RIGHT_TRIGGER       RightTrigger (閾値でデジタル化)
///   OPTIONS                    SDL_GAMEPAD_BUTTON_START             Menu   (ポーズ/中断メニューを開く)
///   SHARE / CREATE             SDL_GAMEPAD_BUTTON_BACK              View
///   L3 / R3                    SDL_GAMEPAD_BUTTON_LEFT_STICK / RIGHT_STICK  LeftThumbstickButton / RightThumbstickButton
///
/// ゲームプレイ中の実際のレトロパッド(RetroJoypad)入力は、EmulationHost内の
/// GamepadInputMapper が処理する。SDLの読み取りと、SDLのボタン番号との対応表は
/// <see cref="PadSnapshot"/> / <see cref="PadButtons"/>(物理位置の名前 <see cref="PadButton"/>)に
/// 集約されており、このenumはそこから変換された「論理名」として使われる:
///   ・メニュー操作(IControllerPage): East=A(決定)、South=B(もどる)  → PadButtons.ToLauncherButton
///   ・保存済みの割り当て・ホットキー設定: South=A、East=B            → PadButtons.FromGamepadButton
/// (A/Bの向きが2通りあるのは既存の設定ファイルとの互換のため。次の段階で設定形式を見直す。)
/// </summary>
public enum GamepadButton
{
    A,
    B,
    X,
    Y,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftShoulder,
    RightShoulder,
    LeftTrigger,
    RightTrigger,
    Menu,
    View,
    LeftThumbstickButton,
    RightThumbstickButton,

    // ---- スティックの傾き(方向ごとの「押した」扱い)。コントローラー設定の割り当て専用で、
    //      ランチャーのメニュー操作には使わない。末尾に追加(既存の保存値を壊さないため)。 ----
    LeftStickUp,
    LeftStickDown,
    LeftStickLeft,
    LeftStickRight,
    RightStickUp,
    RightStickDown,
    RightStickLeft,
    RightStickRight,

    /// <summary>PS4/PS5のタッチパッド押し込み(ランチャーのメニュー操作専用。末尾に追加)。</summary>
    Touchpad,
}

/// <summary>
/// ゲームパッド入力を受け取れるページが実装するインターフェース。
/// MainWindow がフレームの現在ページに対してこれを通じて入力を中継する。
/// </summary>
public interface IControllerPage
{
    /// <summary>ボタンが押された瞬間、および十字キー長押し中のオートリピート時に呼ばれる。</summary>
    void OnGamepadButton(GamepadButton button);

    /// <summary>ボタンが一定時間(既定600ms)以上押され続けたときに一度だけ呼ばれる。</summary>
    void OnGamepadButtonLongPress(GamepadButton button);
}
