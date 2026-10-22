namespace EmulatorLauncher.Input;

/// <summary>ある瞬間の1台のゲームパッドの状態(ボタン・スティック・トリガー)を1回で読み取って固めたもの。</summary>
public readonly struct PadSnapshot
{
    /// <summary>L2/R2(アナログ軸 0〜32767)を「押した」とみなす標準のしきい値(約半分)。</summary>
    public const short TriggerThreshold = 16000;

    private readonly uint _buttons;

    /// <summary>コントローラーが接続されている(読み取れた)か。</summary>
    public bool Connected { get; }

    public short LeftX { get; }
    public short LeftY { get; }
    public short RightX { get; }
    public short RightY { get; }

    /// <summary>L2の軸の値(0〜32767)。</summary>
    public short LeftTriggerValue { get; }

    /// <summary>R2の軸の値(0〜32767)。</summary>
    public short RightTriggerValue { get; }

    private PadSnapshot(uint buttons, short lx, short ly, short rx, short ry, short lt, short rt)
    {
        Connected = true;
        _buttons = buttons;
        LeftX = lx;
        LeftY = ly;
        RightX = rx;
        RightY = ry;
        LeftTriggerValue = lt;
        RightTriggerValue = rt;
    }

    /// <summary>指定ボタンが押されているか(トリガーは 以上で押下)。</summary>
    public bool IsDown(PadButton button)
    {
        if (!Connected) return false;

        return button switch
        {
            PadButton.LeftTrigger => LeftTriggerValue >= TriggerThreshold,
            PadButton.RightTrigger => RightTriggerValue >= TriggerThreshold,
            _ => (_buttons & (1u << (int)button)) != 0,
        };
    }

    /// <summary>どれかのボタン(トリガー含む)が押されているか。</summary>
    public bool AnyDown => Connected
        && (_buttons != 0 || LeftTriggerValue >= TriggerThreshold || RightTriggerValue >= TriggerThreshold);

    /// <summary>SDLのゲームパッドハンドルから現在の状態を読み取る。</summary>
    public static PadSnapshot Capture(nint pad)
    {
        if (pad == nint.Zero) return default;

        uint buttons = 0;
        foreach (PadButton button in PadButtons.All)
        {
            int sdlButton = PadButtons.SdlButtonOf(button);
            if (sdlButton >= 0 && Sdl.SDL_GetGamepadButton(pad, sdlButton))
            {
                buttons |= 1u << (int)button;
            }
        }

        return new PadSnapshot(
            buttons,
            Sdl.SDL_GetGamepadAxis(pad, Sdl.SDL_GAMEPAD_AXIS_LEFTX),
            Sdl.SDL_GetGamepadAxis(pad, Sdl.SDL_GAMEPAD_AXIS_LEFTY),
            Sdl.SDL_GetGamepadAxis(pad, Sdl.SDL_GAMEPAD_AXIS_RIGHTX),
            Sdl.SDL_GetGamepadAxis(pad, Sdl.SDL_GAMEPAD_AXIS_RIGHTY),
            Sdl.SDL_GetGamepadAxis(pad, Sdl.SDL_GAMEPAD_AXIS_LEFT_TRIGGER),
            Sdl.SDL_GetGamepadAxis(pad, Sdl.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER));
    }
}
