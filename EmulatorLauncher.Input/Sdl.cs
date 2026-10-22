using System.Runtime.InteropServices;

namespace EmulatorLauncher.Input;

public static class Sdl
{
    private const string LibName = "SDL3";

    // --- Init / Quit / Error ---
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_Init(uint flags);

    [DllImport(LibName)] public static extern void SDL_Quit();

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_InitSubSystem(uint flags);

    [DllImport(LibName)] public static extern void SDL_QuitSubSystem(uint flags);

    [DllImport(LibName)] public static extern uint SDL_WasInit(uint flags);
    [DllImport(LibName)] private static extern nint SDL_GetError();
    public static string SDL_GetErrorString() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetHint(string name, string value);

    public const string SDL_HINT_JOYSTICK_WGI = "SDL_JOYSTICK_WGI";

    public const string SDL_HINT_JOYSTICK_RAWINPUT = "SDL_JOYSTICK_RAWINPUT";

    public const string SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS = "SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS";

    public const string SDL_HINT_JOYSTICK_HIDAPI = "SDL_JOYSTICK_HIDAPI";
    public const string SDL_HINT_JOYSTICK_HIDAPI_PS5 = "SDL_JOYSTICK_HIDAPI_PS5";

    public const string SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES = "SDL_AUDIO_DEVICE_SAMPLE_FRAMES";

    public const uint SDL_INIT_AUDIO = 0x00000010u;
    public const uint SDL_INIT_VIDEO = 0x00000020u;
    public const uint SDL_INIT_JOYSTICK = 0x00000200u;
    public const uint SDL_INIT_GAMEPAD = 0x00002000u;

    public const int SDL_GL_CONTEXT_MAJOR_VERSION = 17;
    public const int SDL_GL_CONTEXT_MINOR_VERSION = 18;
    public const int SDL_GL_CONTEXT_PROFILE_MASK = 20;

    public const int SDL_GL_CONTEXT_PROFILE_CORE = 0x0001;
    public const int SDL_GL_CONTEXT_PROFILE_COMPATIBILITY = 0x0002;
    public const int SDL_GL_CONTEXT_PROFILE_ES = 0x0004;

    [DllImport(LibName)] public static extern uint SDL_CreateProperties();
    [DllImport(LibName)] public static extern void SDL_DestroyProperties(uint props);

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetPointerProperty(uint props, string name, nint value);

    public const string SDL_PROP_WINDOW_CREATE_WIN32_HWND_POINTER = "SDL.window.create.win32.hwnd";

    public const string SDL_PROP_WINDOW_CREATE_RESIZABLE_BOOLEAN = "SDL.window.create.resizable";

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetWindowResizable(nint window, [MarshalAs(UnmanagedType.I1)] bool resizable);

    public const string SDL_PROP_WINDOW_CREATE_OPENGL_BOOLEAN = "SDL.window.create.opengl";

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetBooleanProperty(uint props, string name, [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    public static extern bool SDL_SetNumberProperty(uint props, string name, long value);

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    public static extern nint SDL_GetPointerProperty(uint props, string name, nint defaultValue);

    [DllImport(LibName)] public static extern uint SDL_GetWindowProperties(nint window);

    [DllImport(LibName)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_HideCursor();
    [DllImport(LibName)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_ShowCursor();

    public const string SDL_PROP_WINDOW_CREATE_X_NUMBER = "SDL.window.create.x";
    public const string SDL_PROP_WINDOW_CREATE_Y_NUMBER = "SDL.window.create.y";
    public const string SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER = "SDL.window.create.width";
    public const string SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER = "SDL.window.create.height";
    public const string SDL_PROP_WINDOW_CREATE_BORDERLESS_BOOLEAN = "SDL.window.create.borderless";
    public const string SDL_PROP_WINDOW_CREATE_HIDDEN_BOOLEAN = "SDL.window.create.hidden";
    public const string SDL_PROP_WINDOW_WIN32_HWND_POINTER = "SDL.window.win32.hwnd";

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetWindowPosition(nint window, int x, int y);

    // --- OpenGL context ---
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GL_SetAttribute(int attr, int value);

    [DllImport(LibName)] public static extern nint SDL_GL_CreateContext(nint window);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GL_MakeCurrent(nint window, nint context);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GL_DestroyContext(nint context);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GL_SwapWindow(nint window);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GL_SetSwapInterval(int interval);

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    public static extern nint SDL_GL_GetProcAddress(string proc);

    // SDL_GLAttr
    public const int SDL_GL_DOUBLEBUFFER = 5;
    public const int SDL_GL_DEPTH_SIZE = 6;
    public const int SDL_GL_STENCIL_SIZE = 7;

    // --- Window / Renderer / Texture ---
    [DllImport(LibName)] public static extern nint SDL_CreateWindowWithProperties(uint props);
    [DllImport(LibName)] public static extern void SDL_DestroyWindow(nint window);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetWindowSize(nint window, int w, int h);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GetWindowSize(nint window, out int w, out int h);

    /// <summary>ウィンドウの実ピクセルサイズ(DPIスケーリング後)。GLの描画先サイズと一致する。</summary>
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GetWindowSizeInPixels(nint window, out int w, out int h);

    /// <summary>保留中のウィンドウ操作(リサイズ等)の完了を待つ。</summary>
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SyncWindow(nint window);

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    public static extern nint SDL_CreateRenderer(nint window, string? name);

    [DllImport(LibName)] public static extern void SDL_DestroyRenderer(nint renderer);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetRenderVSync(nint renderer, int vsync);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetRenderLogicalPresentation(nint renderer, int w, int h, int mode);

    public const int SDL_LOGICAL_PRESENTATION_STRETCH = 1;
    public const int SDL_LOGICAL_PRESENTATION_LETTERBOX = 2;

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_RenderClear(nint renderer);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_RenderTexture(nint renderer, nint texture, nint srcrect, nint dstrect);

    /// <summary>SDL_FRect(浮動小数点の矩形)。SDL_RenderTextureRotated等、回転描画系APIで使う。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_FRect
    {
        public float X;
        public float Y;
        public float W;
        public float H;
    }

    /// <summary>SDL_FPoint(浮動小数点の座標)。SDL_RenderTextureRotatedの回転中心指定に使う。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_FPoint
    {
        public float X;
        public float Y;
    }

    public const int SDL_FLIP_NONE = 0;
    public const int SDL_FLIP_HORIZONTAL = 1;
    public const int SDL_FLIP_VERTICAL = 2;

    /// <summary>
    /// SDL_RenderTexture の回転版。dstrect(テクスチャの元サイズ基準の配置矩形)を、
    /// centerを中心にangle度(時計回り)回転させて描画する。
    /// RETRO_ENVIRONMENT_SET_ROTATION(反時計回り基準)を扱う側で符号変換すること。
    /// </summary>
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_RenderTextureRotated(
        nint renderer, nint texture, nint srcrect, ref SDL_FRect dstrect,
        double angle, ref SDL_FPoint center, int flip);

    [DllImport(LibName)] public static extern void SDL_RenderPresent(nint renderer);

    // 早送り中の速度インジケーター(セグメント風の塗りつぶし矩形)描画用。
    // フォントを使わず単純な矩形の集合で数字を描くため、テクスチャ/フォント読み込みは不要。
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetRenderDrawColor(nint renderer, byte r, byte g, byte b, byte a);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetRenderDrawBlendMode(nint renderer, uint blendMode);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_RenderFillRect(nint renderer, ref SDL_FRect rect);

    [DllImport(LibName)]
    public static extern nint SDL_CreateTexture(nint renderer, uint format, int access, int w, int h);

    [DllImport(LibName)] public static extern void SDL_DestroyTexture(nint texture);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_UpdateTexture(nint texture, nint rect, nint pixels, int pitch);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetTextureBlendMode(nint texture, uint blendMode);

    public const uint SDL_BLENDMODE_NONE = 0x00000000u;
    public const uint SDL_BLENDMODE_BLEND = 0x00000001u;
    public const uint SDL_BLENDMODE_ADD = 0x00000002u;
    public const uint SDL_BLENDMODE_MOD = 0x00000004u;

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetTextureScaleMode(nint texture, uint scaleMode);

    public const uint SDL_SCALEMODE_NEAREST = 0;
    public const uint SDL_SCALEMODE_LINEAR = 1;
    public const uint SDL_SCALEMODE_BEST = 2;
    public const uint SDL_SCALEMODE_ANISOTROPIC = 3;

    public const int SDL_TEXTUREACCESS_STREAMING = 1;

    public const uint SDL_PIXELFORMAT_XRGB1555 = 0x15130f02u; // RETRO_PIXEL_FORMAT_0RGB1555
    public const uint SDL_PIXELFORMAT_RGB565 = 0x15151002u;
    public const uint SDL_PIXELFORMAT_ARGB8888 = 0x16362004u;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct SDL_Event
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(16)] public uint which;
        // SDL_GamepadSensorEvent(SDL_EVENT_GAMEPAD_SENSOR_UPDATE)用
        [FieldOffset(20)] public int sensor;
        [FieldOffset(24)] public float sensorX;
        [FieldOffset(28)] public float sensorY;
        [FieldOffset(32)] public float sensorZ;
        [FieldOffset(40)] public ulong sensorTimestampNs;
    }

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_PollEvent(out SDL_Event ev);

    [DllImport(LibName)] public static extern void SDL_PumpEvents();

    public const uint SDL_EVENT_QUIT = 0x100;

    public const uint SDL_EVENT_GAMEPAD_ADDED = 0x653;
    public const uint SDL_EVENT_GAMEPAD_REMOVED = 0x654;
    public const uint SDL_EVENT_GAMEPAD_SENSOR_UPDATE = 0x659;

    [DllImport(LibName)] public static extern uint SDL_GetGamepadID(nint gamepad);

    [DllImport(LibName)] public static extern nint SDL_GetKeyboardState(out int numkeys);

    // --- マウス(タッチ入力の代替として使う。RETRO_DEVICE_POINTER参照) ---
    // SDL_GetMouseStateはSDL3では「マウスフォーカスを持つウィンドウ」基準の
    // ウィンドウ相対座標を返す。どのウィンドウがフォーカスを持っているかは
    // SDL_GetMouseFocusで判定できるので、それが目的の描画ウィンドウ(_sdlWindow)と
    // 一致する間だけ座標を信頼する、という使い方をする。
    [DllImport(LibName)] public static extern uint SDL_GetMouseState(out float x, out float y);
    [DllImport(LibName)] public static extern nint SDL_GetMouseFocus();

    // SDL_BUTTON_MASK(SDL_BUTTON_LEFT) = 1 << (1-1) = 0x1
    public const uint SDL_BUTTON_LMASK = 1u << 0;

    public const int SDL_SCANCODE_UP = 82;
    public const int SDL_SCANCODE_DOWN = 81;
    public const int SDL_SCANCODE_LEFT = 80;
    public const int SDL_SCANCODE_RIGHT = 79;
    public const int SDL_SCANCODE_RETURN = 40;
    public const int SDL_SCANCODE_RSHIFT = 229;
    public const int SDL_SCANCODE_Z = 29;
    public const int SDL_SCANCODE_X = 27;
    public const int SDL_SCANCODE_A = 4;
    public const int SDL_SCANCODE_S = 22;
    public const int SDL_SCANCODE_Q = 20;
    public const int SDL_SCANCODE_W = 26;

    [DllImport(LibName)] public static extern nint SDL_GetGamepads(out int count);
    [DllImport(LibName)] public static extern void SDL_free(nint mem);

    [DllImport(LibName)] public static extern nint SDL_OpenGamepad(uint instanceId);
    [DllImport(LibName)] public static extern void SDL_CloseGamepad(nint gamepad);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GetGamepadButton(nint gamepad, int button);

    [DllImport(LibName)] public static extern short SDL_GetGamepadAxis(nint gamepad, int axis);

    // センサー(ジャイロ/加速度。SDL_SENSOR_ACCEL=1、SDL_SENSOR_GYRO=2)と振動。
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GamepadHasSensor(nint gamepad, int type);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetGamepadSensorEnabled(nint gamepad, int type, [MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_GetGamepadSensorData(nint gamepad, int type, [Out] float[] data, int numValues);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_RumbleGamepad(nint gamepad, ushort lowFrequency, ushort highFrequency, uint durationMs);

    [DllImport(LibName)] public static extern int SDL_GetGamepadType(nint gamepad);

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    public static extern nint SDL_GetGamepadName(nint gamepad);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetGamepadLED(nint gamepad, byte red, byte green, byte blue);

    [DllImport(LibName)] public static extern ushort SDL_GetGamepadVendor(nint gamepad);
    [DllImport(LibName)] public static extern ushort SDL_GetGamepadProduct(nint gamepad);
    [DllImport(LibName)] public static extern nint SDL_GetGamepadSerial(nint gamepad);


    public const int SDL_GAMEPAD_TYPE_STANDARD = 1;
    public const int SDL_GAMEPAD_TYPE_XBOX360 = 2;
    public const int SDL_GAMEPAD_TYPE_XBOXONE = 3;
    public const int SDL_GAMEPAD_TYPE_PS4 = 5;
    public const int SDL_GAMEPAD_TYPE_PS5 = 6;
    public const int SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_PRO = 7;
    public const int SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_LEFT = 8;
    public const int SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_RIGHT = 9;
    public const int SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_PAIR = 10;

    public const int SDL_GAMEPAD_BUTTON_SOUTH = 0;          // Xbox A / Switch B / PS Cross(✁E
    public const int SDL_GAMEPAD_BUTTON_EAST = 1;            // Xbox B / Switch A / PS Circle(◁E
    public const int SDL_GAMEPAD_BUTTON_WEST = 2;             // Xbox X / Switch Y / PS Square(□)
    public const int SDL_GAMEPAD_BUTTON_NORTH = 3;            // Xbox Y / Switch X / PS Triangle(△)
    public const int SDL_GAMEPAD_BUTTON_BACK = 4;
    public const int SDL_GAMEPAD_BUTTON_GUIDE = 5;
    public const int SDL_GAMEPAD_BUTTON_START = 6;
    public const int SDL_GAMEPAD_BUTTON_LEFT_STICK = 7;
    public const int SDL_GAMEPAD_BUTTON_RIGHT_STICK = 8;
    public const int SDL_GAMEPAD_BUTTON_LEFT_SHOULDER = 9;
    public const int SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER = 10;
    public const int SDL_GAMEPAD_BUTTON_DPAD_UP = 11;
    public const int SDL_GAMEPAD_BUTTON_DPAD_DOWN = 12;
    public const int SDL_GAMEPAD_BUTTON_DPAD_LEFT = 13;
    public const int SDL_GAMEPAD_BUTTON_DPAD_RIGHT = 14;
    public const int SDL_GAMEPAD_BUTTON_TOUCHPAD = 20;       // PS4/PS5のタッチパッド押し込み

    public const int SDL_GAMEPAD_AXIS_LEFTX = 0;
    public const int SDL_GAMEPAD_AXIS_LEFTY = 1;
    public const int SDL_GAMEPAD_AXIS_RIGHTX = 2;
    public const int SDL_GAMEPAD_AXIS_RIGHTY = 3;
    public const int SDL_GAMEPAD_AXIS_LEFT_TRIGGER = 4;
    public const int SDL_GAMEPAD_AXIS_RIGHT_TRIGGER = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_AudioSpec
    {
        public int format;   // SDL_AudioFormat
        public int channels;
        public int freq;
    }

    public const int SDL_AUDIO_S16 = 0x8010; // SDL_AUDIO_S16LE

    public const uint SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK = 0xFFFFFFFFu;

    [DllImport(LibName)]
    public static extern nint SDL_OpenAudioDeviceStream(
        uint devid, ref SDL_AudioSpec spec, nint callback, nint userdata);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_ResumeAudioStreamDevice(nint stream);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_PauseAudioStreamDevice(nint stream);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_ClearAudioStream(nint stream);

    // 早送り中の音声を「ミュートせず、ピッチシフトして倍速再生する」ために使う。
    // srcspecでアプリ側が渡すデータの想定サンプルレートを実際より高く申告すると、
    // SDLが内部でdstspec(通常はnint.Zero=変更なし=デバイスの実フォーマットのまま)へ
    // 自動的にダウンサンプリングしてくれるため、結果として「短い時間に圧縮されて
    // (ピッチが上がった状態で)」再生される。dstspecにnint.Zeroを渡すとその側は
    // 現状維持になる(SDL3の仕様)。
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetAudioStreamFormat(nint stream, ref SDL_AudioSpec srcspec, nint dstspec);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_PutAudioStreamData(nint stream, byte[] buf, int len);

    // 再生速度の微調整(1.0=等倍)。音声キューの溜まり具合に応じてわずかに速く/遅く再生し、
    // コアの音声生産量と再生量のずれ(遅延が積み上がる現象)を吸収するのに使う。
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetAudioStreamFrequencyRatio(nint stream, float ratio);

    // 音量(1.0=等倍、0.5=半分)。ゲームの音量設定に使う。
    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetAudioStreamGain(nint stream, float gain);

    [DllImport(LibName)] public static extern int SDL_GetAudioStreamQueued(nint stream);
    [DllImport(LibName)] public static extern void SDL_DestroyAudioStream(nint stream);
}