using System;
using System.Runtime.InteropServices;

namespace EmulatorLauncher.Interop;

/// <summary>OpenGLのP/Invoke宣言。</summary>
internal static class Gl
{
    private const string LibName = "opengl32.dll";

    // OpenGL 1.1 (opengl32.dllから直接エクスポートされている)
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glViewport(int x, int y, int width, int height);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glClearColor(float r, float g, float b, float a);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glClear(uint mask);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glEnable(uint cap);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glDisable(uint cap);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glGenTextures(int n, out uint textures);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glDeleteTextures(int n, ref uint textures);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glBindTexture(uint target, uint texture);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glTexParameteri(uint target, uint pname, int param);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glTexImage2D(uint target, int level, int internalFormat, int width, int height,
        int border, uint format, uint type, nint pixels);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glTexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height,
        uint format, uint type, nint pixels);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glMatrixMode(uint mode);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glLoadIdentity();

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glOrtho(double left, double right, double bottom, double top, double zNear, double zFar);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glBegin(uint mode);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glEnd();

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glTexCoord2f(float s, float t);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glVertex2f(float x, float y);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glColor4f(float r, float g, float b, float a);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint glGetError();

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glPixelStorei(uint pname, int param);

    // 定数(必要なものだけ抜粋)
    public const uint GL_TEXTURE_2D = 0x0DE1;
    public const uint GL_RGB = 0x1907;
    public const uint GL_RGBA = 0x1908;
    public const uint GL_RGBA8 = 0x8058;
    public const uint GL_UNSIGNED_BYTE = 0x1401;
    public const uint GL_UNPACK_ALIGNMENT = 0x0CF5;
    public const uint GL_LINEAR = 0x2601;
    public const uint GL_NEAREST = 0x2600;
    public const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    public const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    public const uint GL_TEXTURE_WRAP_S = 0x2802;
    public const uint GL_TEXTURE_WRAP_T = 0x2803;
    public const uint GL_CLAMP_TO_EDGE = 0x812F;
    public const uint GL_COLOR_BUFFER_BIT = 0x4000;
    public const uint GL_DEPTH_BUFFER_BIT = 0x0100;
    public const uint GL_STENCIL_BUFFER_BIT = 0x0400;
    public const uint GL_DEPTH_TEST = 0x0B71;
    public const uint GL_MODELVIEW = 0x1700;
    public const uint GL_PROJECTION = 0x1701;
    public const uint GL_QUADS = 0x0007;

    // フレームバッファオブジェクト(FBO)関連の定数(GL_ARB_framebuffer_object / GL3.0コア)
    public const uint GL_FRAMEBUFFER = 0x8D40;
    public const uint GL_READ_FRAMEBUFFER = 0x8CA8;
    public const uint GL_DRAW_FRAMEBUFFER = 0x8CA9;
    public const uint GL_RENDERBUFFER = 0x8D41;
    public const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    public const uint GL_DEPTH_ATTACHMENT = 0x8D00;
    public const uint GL_STENCIL_ATTACHMENT = 0x8D20;
    public const uint GL_DEPTH_STENCIL_ATTACHMENT = 0x821A;
    public const uint GL_DEPTH24_STENCIL8 = 0x88F0;
    public const uint GL_DEPTH_COMPONENT24 = 0x81A6;
    public const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;

    // 動的ロードするFBO関連関数(opengl32.dllには無いのでSDL_GL_GetProcAddressで取得する)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLGENFRAMEBUFFERS(int n, out uint framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLDELETEFRAMEBUFFERS(int n, ref uint framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLBINDFRAMEBUFFER(uint target, uint framebuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLFRAMEBUFFERTEXTURE2D(uint target, uint attachment, uint textarget, uint texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLGENRENDERBUFFERS(int n, out uint renderbuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLDELETERENDERBUFFERS(int n, ref uint renderbuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLBINDRENDERBUFFER(uint target, uint renderbuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLRENDERBUFFERSTORAGE(uint target, uint internalFormat, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLFRAMEBUFFERRENDERBUFFER(uint target, uint attachment, uint renderbuffertarget, uint renderbuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint PFNGLCHECKFRAMEBUFFERSTATUS(uint target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLBLITFRAMEBUFFER(int srcX0, int srcY0, int srcX1, int srcY1,
        int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PFNGLBINDBUFFER(uint target, uint buffer);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glReadBuffer(uint mode);

    public const uint GL_PIXEL_PACK_BUFFER = 0x88EB;
    public const uint GL_PIXEL_PACK_BUFFER_BINDING = 0x88ED;

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)]
    public static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, nint pixels);

    public const uint GL_BGRA = 0x80E1;
    public const uint GL_PACK_ALIGNMENT = 0x0D05;

    public static PFNGLGENFRAMEBUFFERS glGenFramebuffers = null!;
    public static PFNGLDELETEFRAMEBUFFERS glDeleteFramebuffers = null!;
    public static PFNGLBINDFRAMEBUFFER glBindFramebuffer = null!;
    public static PFNGLFRAMEBUFFERTEXTURE2D glFramebufferTexture2D = null!;
    public static PFNGLGENRENDERBUFFERS glGenRenderbuffers = null!;
    public static PFNGLDELETERENDERBUFFERS glDeleteRenderbuffers = null!;
    public static PFNGLBINDRENDERBUFFER glBindRenderbuffer = null!;
    public static PFNGLRENDERBUFFERSTORAGE glRenderbufferStorage = null!;
    public static PFNGLFRAMEBUFFERRENDERBUFFER glFramebufferRenderbuffer = null!;
    public static PFNGLCHECKFRAMEBUFFERSTATUS glCheckFramebufferStatus = null!;
    public static PFNGLBLITFRAMEBUFFER glBlitFramebuffer = null!;
    public static PFNGLBINDBUFFER glBindBuffer = null!;

    private static bool _loaded;

    /// <summary>FBO関連の関数ポインタを getProcAddress (通常は SDL_GL_GetProcAddress) 経由で解決する。</summary>
    public static void Load(Func<string, nint> getProcAddress)
    {
        if (_loaded) return;

        glGenFramebuffers = Bind<PFNGLGENFRAMEBUFFERS>(getProcAddress, "glGenFramebuffers");
        glDeleteFramebuffers = Bind<PFNGLDELETEFRAMEBUFFERS>(getProcAddress, "glDeleteFramebuffers");
        glBindFramebuffer = Bind<PFNGLBINDFRAMEBUFFER>(getProcAddress, "glBindFramebuffer");
        glFramebufferTexture2D = Bind<PFNGLFRAMEBUFFERTEXTURE2D>(getProcAddress, "glFramebufferTexture2D");
        glGenRenderbuffers = Bind<PFNGLGENRENDERBUFFERS>(getProcAddress, "glGenRenderbuffers");
        glDeleteRenderbuffers = Bind<PFNGLDELETERENDERBUFFERS>(getProcAddress, "glDeleteRenderbuffers");
        glBindRenderbuffer = Bind<PFNGLBINDRENDERBUFFER>(getProcAddress, "glBindRenderbuffer");
        glRenderbufferStorage = Bind<PFNGLRENDERBUFFERSTORAGE>(getProcAddress, "glRenderbufferStorage");
        glFramebufferRenderbuffer = Bind<PFNGLFRAMEBUFFERRENDERBUFFER>(getProcAddress, "glFramebufferRenderbuffer");
        glCheckFramebufferStatus = Bind<PFNGLCHECKFRAMEBUFFERSTATUS>(getProcAddress, "glCheckFramebufferStatus");
        glBlitFramebuffer = Bind<PFNGLBLITFRAMEBUFFER>(getProcAddress, "glBlitFramebuffer");
        glBindBuffer = Bind<PFNGLBINDBUFFER>(getProcAddress, "glBindBuffer");

        _loaded = true;
    }

    private static T Bind<T>(Func<string, nint> getProcAddress, string name) where T : Delegate
    {
        nint ptr = getProcAddress(name);
        if (ptr == nint.Zero)
            throw new InvalidOperationException($"OpenGL関数 '{name}' の取得に失敗しました(FBO/GL3.0対応のGPUドライバが必要です)。");

        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }
}
