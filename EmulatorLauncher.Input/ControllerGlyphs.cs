namespace EmulatorLauncher.Input;

/// <summary>操作ガイドに出すボタン表示の切り替え用。</summary>
public static class ControllerGlyphs
{
    /// <summary>1PがPlayStation系(PS3/PS4/PS5)か。</summary>
    public static bool IsPlayStationPrimary()
    {
        try
        {
            nint pad = GamepadSlots.GetHandle(0);
            if (pad == nint.Zero) return false;

            int type = Sdl.SDL_GetGamepadType(pad);
            return type == 4 /* PS3 */ || type == Sdl.SDL_GAMEPAD_TYPE_PS4 || type == Sdl.SDL_GAMEPAD_TYPE_PS5;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>1PがNintendo Switch系(Proコントローラ/Joy-Con)か。</summary>
    public static bool IsNintendoPrimary()
    {
        try
        {
            nint pad = GamepadSlots.GetHandle(0);
            if (pad == nint.Zero) return false;

            int type = Sdl.SDL_GetGamepadType(pad);
            return type == Sdl.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_PRO
                || type == Sdl.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_LEFT
                || type == Sdl.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_RIGHT
                || type == Sdl.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_PAIR;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>1PがXbox系(Xbox 360/One、一般的な標準配置のパッド)か。</summary>
    public static bool IsXboxPrimary()
    {
        try
        {
            nint pad = GamepadSlots.GetHandle(0);
            if (pad == nint.Zero) return false;

            int type = Sdl.SDL_GetGamepadType(pad);
            return type == Sdl.SDL_GAMEPAD_TYPE_STANDARD
                || type == Sdl.SDL_GAMEPAD_TYPE_XBOX360
                || type == Sdl.SDL_GAMEPAD_TYPE_XBOXONE;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>操作ガイドの文字キー("A"/"B"/"X"/"Y")を、1Pのコントローラーの実際の印字にする。</summary>
    public static string TranslateHintKeyForPad(string key)
    {
        if (IsXboxPrimary()) return key switch { "A" => "B", "B" => "A", _ => key };
        if (IsNintendoPrimary()) return key switch { "X" => "Y", "Y" => "X", _ => key };
        return key;
    }

    /// <summary>表示キー("A"/"B"/"X"/"Y")に対応するPSの記号と色(R,G,B)を返す。</summary>
    public static bool TryGetPlayStationSymbol(string key, out string symbol, out (byte R, byte G, byte B) color)
    {
        switch (key)
        {
            case "A": symbol = "○"; color = (0xEE, 0x4B, 0x55); return true;
            case "B": symbol = "✕"; color = (0x5B, 0x8D, 0xEF); return true;
            case "X": symbol = "□"; color = (0xD9, 0x6B, 0xAE); return true;
            case "Y": symbol = "△"; color = (0x2F, 0xBF, 0x9B); return true;
            default: symbol = string.Empty; color = default; return false;
        }
    }

    /// <summary>コントローラー設定画面でボタン名の表記を切り替えるための種類。</summary>
    public enum PadStyle
    {
        /// <summary>A/B/X/Y(下=A、右=B、左=X、上=Y)、L1/R1/L2/R2 など(PS・Switch以外、または未接続)。</summary>
        Standard,

        /// <summary>Xbox系(標準配置)。</summary>
        Xbox,

        /// <summary>Nintendo Switch系。</summary>
        Nintendo,

        /// <summary>○ ✕ □ △、L1/R1/L2/R2 など。</summary>
        PlayStation,
    }

    /// <summary>1Pのコントローラーの表記の種類。</summary>
    public static PadStyle GetPrimaryPadStyle()
        => IsPlayStationPrimary() ? PadStyle.PlayStation
         : IsNintendoPrimary() ? PadStyle.Nintendo
         : IsXboxPrimary() ? PadStyle.Xbox
         : PadStyle.Standard;

    /// <summary>Assets/Controller/ 以下の、そのコントローラーの画像の名前(拡張子なし)。</summary>
    public static string GetControllerImageName(PadStyle style) => style == PadStyle.PlayStation ? "ps5" : "standard";

    /// <summary>設定ファイルの物理ボタン名(GamepadButton。A=下/南、B=右/東、X=左/西、Y=上/北)を、実際のコントローラーの表記にする。</summary>
    public static string GetButtonLabel(GamepadButton button, PadStyle style)
    {
        if (style == PadStyle.PlayStation)
        {
            switch (button)
            {
                case GamepadButton.A: return "✕";
                case GamepadButton.B: return "○";
                case GamepadButton.X: return "□";
                case GamepadButton.Y: return "△";
                case GamepadButton.LeftShoulder: return "L1";
                case GamepadButton.RightShoulder: return "R1";
                case GamepadButton.LeftTrigger: return "L2";
                case GamepadButton.RightTrigger: return "R2";
                case GamepadButton.Menu: return "OPTIONS";
                case GamepadButton.View: return "CREATE";
            }
        }

        if (style == PadStyle.Nintendo)
        {
            switch (button)
            {
                case GamepadButton.A: return "B";
                case GamepadButton.B: return "A";
                case GamepadButton.X: return "Y";
                case GamepadButton.Y: return "X";
            }
        }

        return ControllerMappingService.GetPhysicalButtonShortLabel(button);
    }

    /// <summary>ボタンの説明つき表記(設定画面の選択肢用)。</summary>
    public static string GetButtonDisplayName(GamepadButton button, PadStyle style)
    {
        if (style == PadStyle.Nintendo)
        {
            switch (button)
            {
                case GamepadButton.A: return "B(下)";
                case GamepadButton.B: return "A(右)";
                case GamepadButton.X: return "Y(左)";
                case GamepadButton.Y: return "X(上)";
            }
        }

        return ControllerMappingService.GetPhysicalButtonDisplayName(button);
    }
}
