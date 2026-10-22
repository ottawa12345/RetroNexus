using System.Runtime.InteropServices;

namespace EmulatorLauncher.Emulation;

/// <summary>retro_pixel_format (RETRO_ENVIRONMENT_SET_PIXEL_FORMAT で渡される値)</summary>
public enum RetroPixelFormat : int
{
    /// <summary>0RGB1555 (libretroのデフォルト)</summary>
    XRGB1555 = 0,
    /// <summary>XRGB8888</summary>
    XRGB8888 = 1,
    /// <summary>RGB565</summary>
    RGB565 = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct RetroSystemInfoRaw
{
    public nint library_name;      // const char*
    public nint library_version;   // const char*
    public nint valid_extensions;  // const char*
    [MarshalAs(UnmanagedType.I1)] public bool need_fullpath;
    [MarshalAs(UnmanagedType.I1)] public bool block_extract;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroGameGeometry
{
    public uint base_width;
    public uint base_height;
    public uint max_width;
    public uint max_height;
    public float aspect_ratio;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroSystemTiming
{
    public double fps;
    public double sample_rate;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroSystemAvInfo
{
    public RetroGameGeometry geometry;
    public RetroSystemTiming timing;
}

[StructLayout(LayoutKind.Sequential)]
public struct retro_variable
{
    public string key;
    public string value;
}

/// <summary>
/// retro_variable のネイティブ表現(ポインタそのまま)。
/// SET_VARIABLES で渡ってくる配列を「keyがNULLになるまで」走査するために使う。
/// GET_VARIABLE では、この構造体の2番目のフィールド(value)だけを
/// Marshal.WriteIntPtr で上書きする(keyは触らない)。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RetroVariableRaw
{
    public nint key;   // const char*
    public nint value; // const char* ("説明文; デフォルト値|選択肢2|選択肢3" 形式)
}

[StructLayout(LayoutKind.Sequential)]
internal struct RetroGameInfoRaw
{
    public nint path;   // const char*
    public nint data;   // const void*
    public nuint size;
    public nint meta;   // const char*
}

/// <summary>コア読み込み後に取得できる基本情報(managed版)</summary>
public sealed class RetroSystemInfo
{
    public string LibraryName { get; init; } = "";
    public string LibraryVersion { get; init; } = "";
    public string ValidExtensions { get; init; } = "";
    public bool NeedFullPath { get; init; }
    public bool BlockExtract { get; init; }
}

/// <summary>RETRO_ENVIRONMENT_* コマンド番号(実装している/よく使われるものの抜粋)</summary>
public static class RetroEnvironment
{
    public const uint SET_ROTATION = 1;
    public const uint GET_OVERSCAN = 2;
    public const uint GET_CAN_DUPE = 3;
    public const uint SET_MESSAGE = 6;
    public const uint SHUTDOWN = 7;
    public const uint SET_PERFORMANCE_LEVEL = 8;
    public const uint GET_SYSTEM_DIRECTORY = 9;
    public const uint SET_PIXEL_FORMAT = 10;
    public const uint SET_INPUT_DESCRIPTORS = 11;
    public const uint SET_CONTROLLER_INFO = 35;
    public const uint SET_KEYBOARD_CALLBACK = 12;
    public const uint SET_DISK_CONTROL_INTERFACE = 13;
    public const uint SET_HW_RENDER = 14;
    public const uint GET_VARIABLE = 15;
    public const uint SET_VARIABLES = 16;
    public const uint GET_VARIABLE_UPDATE = 17;
    public const uint SET_SUPPORT_NO_GAME = 18;
    public const uint GET_LIBRETRO_PATH = 19;
    public const uint SET_FRAME_TIME_CALLBACK = 21;
    public const uint SET_AUDIO_CALLBACK = 22;
    public const uint GET_RUMBLE_INTERFACE = 23;
    public const uint GET_INPUT_DEVICE_CAPABILITIES = 24;
    public const uint GET_SENSOR_INTERFACE = 25 | 0x10000; // RETRO_ENVIRONMENT_EXPERIMENTAL
    public const uint GET_LOG_INTERFACE = 27;
    public const uint GET_SAVE_DIRECTORY = 31;
    public const uint SET_GEOMETRY = 37;
    public const uint GET_USERNAME = 38;
    public const uint GET_LANGUAGE = 39;
    public const uint SET_CORE_OPTIONS = 53;
    public const uint SET_CORE_OPTIONS_INTL = 54;
    public const uint SET_CORE_OPTIONS_V2 = 67;
    public const uint SET_CORE_OPTIONS_V2_INTL = 68;
    public const uint GET_DISK_CONTROL_INTERFACE_VERSION = 57;
    public const uint SET_DISK_CONTROL_EXT_INTERFACE = 58;
}

/// <summary>
/// RETRO_MEMORY_* (retro_get_memory_data / retro_get_memory_size に渡すメモリ種別)。
/// SAVE_RAM はカートリッジのバッテリーバックアップRAM(いわゆる「ゲーム内セーブ」)、
/// RTC はGB/GBA等のカートリッジ内蔵時計の状態。
/// </summary>
public static class RetroMemory
{
    public const uint SAVE_RAM = 0;
    public const uint RTC = 1;
    public const uint SYSTEM_RAM = 2;
    public const uint VIDEO_RAM = 3;
}

/// <summary>retro_device (ポートに接続されているデバイス種別)</summary>
public static class RetroDevice
{
    public const uint NONE = 0;
    public const uint JOYPAD = 1;
    public const uint MOUSE = 2;
    public const uint KEYBOARD = 3;
    public const uint LIGHTGUN = 4;
    public const uint ANALOG = 5;
    public const uint POINTER = 6;
}

/// <summary>RETRO_DEVICE_ID_JOYPAD_* (標準ジョイパッドのボタンID)</summary>
public static class RetroJoypad
{
    public const uint B = 0;
    public const uint Y = 1;
    public const uint SELECT = 2;
    public const uint START = 3;
    public const uint UP = 4;
    public const uint DOWN = 5;
    public const uint LEFT = 6;
    public const uint RIGHT = 7;
    public const uint A = 8;
    public const uint X = 9;
    public const uint L = 10;
    public const uint R = 11;
    public const uint L2 = 12;
    public const uint R2 = 13;
    public const uint L3 = 14;
    public const uint R3 = 15;
}

// ------------------------------------------------------------
// コア → フロントエンド のコールバック関数シグネチャ
// (libretro.h の *_t typedef に対応する delegate)
// ------------------------------------------------------------

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
public delegate bool RetroEnvironmentCallback(uint cmd, nint data);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroVideoRefreshCallback(nint data, uint width, uint height, nuint pitch);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroAudioSampleCallback(short left, short right);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate nuint RetroAudioSampleBatchCallback(nint data, nuint frames);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroInputPollCallback();

/// <summary>retro_keyboard_event_t。コアがRETRO_ENVIRONMENT_SET_KEYBOARD_CALLBACKで登録する、キーの押し/離しの通知先。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroKeyboardEventCallback(
    [MarshalAs(UnmanagedType.I1)] bool down, uint keycode, uint character, ushort keyModifiers);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate short RetroInputStateCallback(uint port, uint device, uint index, uint id);

// ------------------------------------------------------------
// HWレンダリング(RETRO_ENVIRONMENT_SET_HW_RENDER)関連
// N64コア(mupen64plus-next, parallel-n64等)のように、ソフトウェアではなく
// OpenGL等で直接描画するコアに対応するために必要。
// ------------------------------------------------------------

/// <summary>retro_hw_context_type(コアが要求するグラフィックスAPIの種類)</summary>
public enum RetroHwContextType : int
{
    NONE = 0,
    OPENGL = 1,
    OPENGLES2 = 2,
    OPENGL_CORE = 3,
    OPENGLES3 = 4,
    OPENGLES_VERSION = 5,
    VULKAN = 6,
    D3D11 = 7,
    D3D10 = 8,
    D3D12 = 9,
    D3D9 = 10,
}

/// <summary>コアが「GLコンテキストの準備ができた/破棄する」タイミングで呼ぶコールバック。引数無し。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroHwContextResetCallback();

/// <summary>コアが「今描画すべきフレームバッファのID」を問い合わせるコールバック。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate nuint RetroHwGetCurrentFramebufferCallback();

/// <summary>コアがGL関数ポインタを問い合わせるコールバック(SDL_GL_GetProcAddressへ委譲する)。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate nint RetroHwGetProcAddressCallback([MarshalAs(UnmanagedType.LPStr)] string sym);

/// <summary>
/// retro_hw_render_callback のネイティブ表現(x64固定レイアウト)。
/// SET_HW_RENDER で渡されてくる構造体で、コアが context_type/depth/stencil/
/// version_major/version_minor/context_reset/context_destroy を埋めた状態で渡してくる。
/// フロントエンド側は get_current_framebuffer / get_proc_address を埋めて
/// 同じ構造体を書き戻す。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RetroHwRenderCallback
{
    public int context_type;
    public nint context_reset;
    public nint get_current_framebuffer;
    public nint get_proc_address;
    [MarshalAs(UnmanagedType.I1)] public bool depth;
    [MarshalAs(UnmanagedType.I1)] public bool stencil;
    [MarshalAs(UnmanagedType.I1)] public bool bottom_left_origin;
    public uint version_major;
    public uint version_minor;
    [MarshalAs(UnmanagedType.I1)] public bool cache_context;
    public nint context_destroy;
    [MarshalAs(UnmanagedType.I1)] public bool debug_context;
}

/// <summary>HWレンダリング時、video_refreshのdataにこの値(全ビット1のポインタ)が渡ってくる。</summary>
public static class RetroHwFrameBuffer
{
    public static readonly nint Valid = unchecked((nint)(-1));
}

[StructLayout(LayoutKind.Sequential)]
internal struct RetroLogCallbackRaw
{
    public nint log; // retro_log_printf_t
}

// log自体はC言語の可変引数(printf形式)関数だが、
// Windows x64呼び出し規約は呼び出し側がスタックを調整するため、
// こちら側で全引数を受け取らなくてもスタック破壊は起きない。
// フォーマット文字列だけ受け取り、書式指定子はそのまま出力する簡易実装で十分。
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroLogPrintfCallback(int level, nint fmt, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6);