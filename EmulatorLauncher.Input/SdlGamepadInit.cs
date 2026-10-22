using System;

namespace EmulatorLauncher.Input;

/// <summary>アプリ全体で共有する SDL3 の「ゲームパッド」サブシステムの初期化と終了処理。</summary>
public static class SdlGamepadInit
{
    private static bool _initialized;

    /// <summary>アプリ起動時に一度だけ呼び出す。</summary>
    public static void InitializeOnce()
    {
        if (_initialized) return;

        // ウィンドウがフォーカスを失う(最小化・非アクティブ化)とコントローラ入力が反応しなくなる問題への対策。
        Sdl.SDL_SetHint(Sdl.SDL_HINT_JOYSTICK_WGI, "0");
        Sdl.SDL_SetHint(Sdl.SDL_HINT_JOYSTICK_RAWINPUT, "0");
        Sdl.SDL_SetHint(Sdl.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");

        // PS5(DualSense)はHIDAPI経由(ウィンドウフォーカスに依存しない生HID通信)で扱わせる。
        Sdl.SDL_SetHint(Sdl.SDL_HINT_JOYSTICK_HIDAPI, "1");
        Sdl.SDL_SetHint(Sdl.SDL_HINT_JOYSTICK_HIDAPI_PS5, "1");

        if (!Sdl.SDL_InitSubSystem(Sdl.SDL_INIT_GAMEPAD))
        {
            throw new InvalidOperationException(
                "SDL_InitSubSystem(SDL_INIT_GAMEPAD) に失敗しました: " + Sdl.SDL_GetErrorString());
        }

        _initialized = true;
    }

    /// <summary>アプリ終了時に一度だけ呼び出す。</summary>
    public static void Shutdown()
    {
        if (!_initialized) return;

        Sdl.SDL_QuitSubSystem(Sdl.SDL_INIT_GAMEPAD);
        _initialized = false;
    }
}
