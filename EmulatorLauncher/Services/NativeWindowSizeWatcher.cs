using System;
using EmulatorLauncher.Interop;

namespace EmulatorLauncher.Services;

/// <summary>指定したHWNDへウィンドウプロシージャのサブクラス化(SetWindowSubclass, ComCtl32)を施し、WM_SIZE / WM_WINDOWPOSCHANGED / WM_DPICHANGED を検知するたびに を発生させる。</summary>
public sealed class NativeWindowSizeWatcher : IDisposable
{
    private const nint SubclassId = 1;

    // クライアント領域の最小サイズ(物理ピクセル)。
    public const int MinClientWidth = 1280;
    public const int MinClientHeight = 720;

    private readonly nint _hwnd;
    // ネイティブコードが関数ポインタを保持し続ける間、デリゲートをGCされないようインスタンスフィールドとして保持しておく必要がある(ローカル変数のままだと後から呼ばれたときに解放済みで落ちることがある)。
    private readonly Win32.SUBCLASSPROC _proc;
    private bool _installed;

    /// <summary>WM_SIZE / WM_WINDOWPOSCHANGED / WM_DPICHANGED を受け取るたびに発生する(最小化中は発生しない。クライアント領域が意味を持たないため)。</summary>
    public event Action? SizeOrPositionChanged;

    /// <summary>ウィンドウの最小化状態が変化したときに発生する(true=最小化された, false=復元された)。</summary>
    public event Action<bool>? MinimizedStateChanged;

    /// <summary>このアプリ(自分のプロセス)全体のアクティブ状態が変化したときに発生する(true=アプリがアクティブになった, false=他のアプリへ切り替わって非アクティブになった)。</summary>
    public event Action<bool>? AppActiveStateChanged;

    private bool _isMinimized;
    private bool _isAppActive = true;

    public NativeWindowSizeWatcher(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = SubclassProc;
        _installed = Win32.SetWindowSubclass(_hwnd, _proc, SubclassId, nint.Zero);
    }

    private nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (uMsg == Win32.WM_GETMINMAXINFO)
        {
            // 既定値を先に埋めさせてから、最小トラックサイズだけ上書きする。
            nint result = Win32.DefSubclassProc(hWnd, uMsg, wParam, lParam);
            if (lParam != nint.Zero
                && Win32.GetWindowRect(hWnd, out var wr)
                && Win32.GetClientRect(hWnd, out var cr))
            {
                int extraW = (wr.Right - wr.Left) - (cr.Right - cr.Left);
                int extraH = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
                info.ptMinTrackSize.X = MinClientWidth + Math.Max(0, extraW);
                info.ptMinTrackSize.Y = MinClientHeight + Math.Max(0, extraH);
                System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
            }
            return result;
        }

        if (uMsg == Win32.WM_SIZE)
        {
            bool minimizedNow = (int)wParam == Win32.SIZE_MINIMIZED;
            if (minimizedNow != _isMinimized)
            {
                _isMinimized = minimizedNow;
                MinimizedStateChanged?.Invoke(_isMinimized);
            }

            // 最小化中はクライアント領域のサイズが意味を持たない(0や極小値になる)ため、SizeOrPositionChangedは発生させない。
            if (!minimizedNow)
            {
                SizeOrPositionChanged?.Invoke();
            }
        }
        else if ((uMsg == Win32.WM_WINDOWPOSCHANGED || uMsg == Win32.WM_DPICHANGED) && !_isMinimized)
        {
            SizeOrPositionChanged?.Invoke();
        }
        else if (uMsg == Win32.WM_ACTIVATEAPP)
        {
            // wParamだけを鵜呑みにせず、実際に前面へ来たウィンドウの所属プロセスを確認する(詳細はAppActiveStateChangedのコメント参照)。
            bool reportedActive = wParam != nint.Zero;
            bool actuallyActive = reportedActive || IsForegroundWindowOwnProcess();

            if (actuallyActive != _isAppActive)
            {
                _isAppActive = actuallyActive;
                AppActiveStateChanged?.Invoke(_isAppActive);
            }
        }

        return Win32.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>現在の前面ウィンドウ(GetForegroundWindow)が、自分自身のプロセスに属しているかどうか。</summary>
    private static bool IsForegroundWindowOwnProcess()
    {
        nint foreground = Win32.GetForegroundWindow();
        if (foreground == nint.Zero) return false;

        Win32.GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        uint ownProcessId = (uint)Environment.ProcessId;
        return foregroundProcessId == ownProcessId;
    }

    public void Dispose()
    {
        if (!_installed) return;
        Win32.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        _installed = false;
    }
}
