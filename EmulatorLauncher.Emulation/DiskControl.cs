using System;
using System.Runtime.InteropServices;
using System.Text;

namespace EmulatorLauncher.Emulation;

/// <summary>libretroのディスクコントロールインターフェース(RETRO_ENVIRONMENT_SET_DISK_CONTROL_INTERFACE / _EXT_INTERFACE)のラッパー。</summary>
internal sealed class DiskControl
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SetEjectStateT([MarshalAs(UnmanagedType.I1)] bool ejected);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetEjectStateT();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetImageIndexT();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SetImageIndexT(uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetNumImagesT();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetImageLabelT(uint index, nint buffer, nuint length);

    private SetEjectStateT? _setEjectState;
    private GetEjectStateT? _getEjectState;
    private GetImageIndexT? _getImageIndex;
    private SetImageIndexT? _setImageIndex;
    private GetNumImagesT? _getNumImages;
    private GetImageLabelT? _getImageLabel;

    /// <summary>コアから渡された retro_disk_control_callback(基本7個) / retro_disk_control_ext_callback(基本7個+拡張3個)の関数ポインタ並びを読み取る。</summary>
    public static DiskControl? Create(nint data, bool ext)
    {
        if (data == nint.Zero) return null;

        nint Slot(int i) => Marshal.ReadIntPtr(data, i * nint.Size);

        nint setEject = Slot(0), getEject = Slot(1), getIndex = Slot(2), setIndex = Slot(3), getNum = Slot(4);
        if (setEject == nint.Zero || getIndex == nint.Zero || setIndex == nint.Zero || getNum == nint.Zero)
            return null;

        var dc = new DiskControl
        {
            _setEjectState = Marshal.GetDelegateForFunctionPointer<SetEjectStateT>(setEject),
            _getImageIndex = Marshal.GetDelegateForFunctionPointer<GetImageIndexT>(getIndex),
            _setImageIndex = Marshal.GetDelegateForFunctionPointer<SetImageIndexT>(setIndex),
            _getNumImages = Marshal.GetDelegateForFunctionPointer<GetNumImagesT>(getNum),
        };

        if (getEject != nint.Zero)
            dc._getEjectState = Marshal.GetDelegateForFunctionPointer<GetEjectStateT>(getEject);

        if (ext)
        {
            nint label = Slot(9);
            if (label != nint.Zero)
                dc._getImageLabel = Marshal.GetDelegateForFunctionPointer<GetImageLabelT>(label);
        }

        return dc;
    }

    public int NumImages
    {
        get
        {
            try { return (int)(_getNumImages?.Invoke() ?? 0); }
            catch { return 0; }
        }
    }

    public int ImageIndex
    {
        get
        {
            try { return (int)(_getImageIndex?.Invoke() ?? 0); }
            catch { return 0; }
        }
    }

    public bool IsEjected
    {
        get
        {
            try { return _getEjectState?.Invoke() ?? false; }
            catch { return false; }
        }
    }

    public bool SetEjected(bool ejected)
    {
        try { return _setEjectState?.Invoke(ejected) ?? false; }
        catch { return false; }
    }

    public bool SetImageIndex(int index)
    {
        try { return _setImageIndex?.Invoke((uint)index) ?? false; }
        catch { return false; }
    }

    /// <summary>コアがラベル(例: "Disk 1 Side A")を提供していればそれを返す。</summary>
    public string? GetImageLabel(int index)
    {
        if (_getImageLabel is null) return null;

        const int size = 256;
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteByte(buffer, 0);
            if (!_getImageLabel((uint)index, buffer, (nuint)size)) return null;
            string? text = Marshal.PtrToStringAnsi(buffer);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
