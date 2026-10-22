using System.Runtime.InteropServices;

namespace EmulatorLauncher.Interop;

/// <summary>WinUI3のメインウィンドウ(HWND)に、SDL2描画用のウィンドウを"所有された(owned)"トップレベルウィンドウとして重ねるための最小限のWin32宣言。</summary>
public static class Win32
{
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;

    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;

    public static readonly nint HWND_TOP = 0;
    public static readonly nint HWND_TOPMOST = new(-1);

    // メニュー表示中、SDL描画用ウィンドウ(WS_POPUP)を隠すために使う。
    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8; // フォーカスを奪わずに再表示

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    // マウス入力(RETRO_DEVICE_POINTER / RETRO_DEVICE_MOUSE)用
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_MBUTTON = 0x04;

    // ウィンドウの表示/非表示/破棄時にDWMが付ける縮小・フェードのアニメーションを無効にする。
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    public static void DisableWindowTransitions(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        int on = 1;
        try { DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, sizeof(int)); }
        catch { /* DWMが使えない環境ではアニメーションが出るだけなので無視 */ }
    }

    // ウィンドウの最小サイズ制限(WM_GETMINMAXINFO)用。
    public const uint WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // ウィンドウメッセージの直接フック(サブクラス化) WinUI3のXAMLレイアウト(Measure/Arrange/Render/Composition)は、ウィンドウの最大化/リストア(元に戻す)のように「Win32レベルで一気にHWNDがリサイズされる」操作に対しては、そのHWNDリサイズより後から追いつく形で確定する。
    public delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    public const uint WM_SIZE = 0x0005;
    public const uint WM_WINDOWPOSCHANGED = 0x0047;
    public const uint WM_DPICHANGED = 0x02E0;
    // WM_ACTIVATEAPPは「プロセス」ではなく「GUIスレッド」単位で発生する。
    public const uint WM_ACTIVATEAPP = 0x001C;

    // WM_SIZE の wParam 値(ウィンドウが最小化されたかどうかの判定に使う)。
    public const int SIZE_MINIMIZED = 1;

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    public static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nint uIdSubclass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(
        nint hWnd, int x, int y, int nWidth, int nHeight,
        [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    // Windowsの既定のスレッドスリープ精度は約15.6msと粗く、Thread.Sleep(1)を多用するフレームタイミングループでジッタの主因になる(結果としてオーディオが詰まり気味/薄すぎになり、プチプチ音に繋がる)。
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uPeriod);

    // MMCSS(Multimedia Class Scheduler Service)への参加。
    [DllImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true)]
    public static extern nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(nint avrtHandle);

    // MainWindow(オーナー)の所有ウィンドウ(owned window)として扱われるよう、SDLが作成したネイティブウィンドウへ設定するために使う。
    public const int GWLP_HWNDPARENT = -8;

    [DllImport("user32.dll")]
    public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}