using System.Runtime.InteropServices;
using EmulatorLauncher.Models;
using Microsoft.Win32.SafeHandles;

namespace EmulatorLauncher.RomLibrary;

/// <summary>
/// NTFS(またはReFS)上のファイル/フォルダを一意に識別するための情報
/// (ボリュームシリアル番号 + 128bitのファイルID)を取得する。
///
/// <see cref="RomFolder.FolderId"/>/<see cref="RomFolder.VolumeSerial"/>、
/// <see cref="RomFile.FileId"/>/<see cref="RomFile.VolumeSerial"/>は、
/// この <see cref="TryGetFileIdentity"/> が返す値をそのまま保存する想定。
///
/// Win32の<c>GetFileInformationByHandleEx</c>を<c>FileIdInfo</c>クラスで呼ぶと、
/// <c>FILE_ID_INFO{ VolumeSerialNumber(8byte), FileId(=FILE_ID_128, 16byte) }</c>が
/// 一度に取得できる。この16byteのFileIdはそのまま<see cref="Guid"/>として扱える
/// (どちらも128bit)ため、RomFolder/RomFileのGuidフィールドと自然に対応する。
/// ドライブレターやパス文字列が変わっても、同じ物理フォルダ/ファイルである限り
/// この値は変わらない。
/// </summary>
internal static class NativeFileId
{
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;

    // ディレクトリのハンドルをCreateFileで開くために必須のフラグ
    // (これが無いとフォルダに対してCreateFileはERROR_ACCESS_DENIEDで失敗する)。
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    // FILE_INFO_BY_HANDLE_CLASS列挙体のうち、今回使う FileIdInfo の値。
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;

        // ネイティブ側のFILE_ID_128は16byteの固定長配列。ByValArrayでその場に
        // インライン展開されるので、unsafe/fixedを使わずにマーシャリングできる。
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FILE_ID_INFO fileInformation,
        uint dwBufferSize);

    /// <summary>
    /// 指定したファイル/フォルダのNTFS識別情報(ボリュームシリアル番号 + 128bitのファイルID)を取得する。
    /// 取得できた場合はtrue、パスが存在しない/権限が無い場合はfalseを返す。
    /// FAT32/exFAT等はパスのハッシュによる代用IDを返す(例外は投げない)。
    /// </summary>
    public static bool TryGetFileIdentity(string path, out ulong volumeSerial, out Guid fileOrFolderId)
    {
        volumeSerial = 0;
        fileOrFolderId = Guid.Empty;

        using SafeFileHandle handle = CreateFile(
            path,
            dwDesiredAccess: 0, // メタデータの取得だけが目的なので、読み書き権限は要求しない
            dwShareMode: FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            lpSecurityAttributes: 0,
            dwCreationDisposition: OPEN_EXISTING,
            dwFlagsAndAttributes: FILE_FLAG_BACKUP_SEMANTICS,
            hTemplateFile: 0);

        if (handle.IsInvalid)
        {
            return false;
        }

        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out FILE_ID_INFO info,
                (uint)Marshal.SizeOf<FILE_ID_INFO>()))
        {
            // FAT32/exFAT等、ファイルIDを持たないファイルシステム。ボリュームのシリアル番号と、
            // ドライブ直下からのパスのハッシュで代用する(ドライブレターが変わっても同じ値になる)。
            // この代用IDはパスが変わると変わるため、名前変更の追跡はできない(削除+新規として扱われる)。
            if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION basic))
            {
                return false;
            }

            string full = Path.GetFullPath(path);
            string relative = full[(Path.GetPathRoot(full)?.Length ?? 0)..]
                .TrimEnd('\\').ToLowerInvariant();
            volumeSerial = basic.VolumeSerialNumber;
            fileOrFolderId = new Guid(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(relative)));
            return true;
        }

        volumeSerial = info.VolumeSerialNumber;
        fileOrFolderId = new Guid(info.FileId);
        return true;
    }
}
