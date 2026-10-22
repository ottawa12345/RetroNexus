using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using EmulatorLauncher.Services;

namespace EmulatorLauncher.Emulation;

/// <summary>libretroコア(.dll)をロードし、retro_* 関数群をC#から直接呼び出すための薄いラッパー。</summary>
public sealed class LibretroCore : IDisposable
{
    private readonly nint _handle;

    // コアがエクスポートする retro_* 関数のシグネチャ
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroInitT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroDeinitT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint RetroApiVersionT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroGetSystemInfoT(out RetroSystemInfoRaw info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroGetSystemAvInfoT(out RetroSystemAvInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetEnvironmentT(RetroEnvironmentCallback cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetVideoRefreshT(RetroVideoRefreshCallback cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetAudioSampleT(RetroAudioSampleCallback cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetAudioSampleBatchT(RetroAudioSampleBatchCallback cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetInputPollT(RetroInputPollCallback cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetInputStateT(RetroInputStateCallback cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetControllerPortDeviceT(uint port, uint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroResetT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroRunT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RetroLoadGameT(nint game);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroUnloadGameT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint RetroSerializeSizeT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool RetroSerializeT(nint data, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool RetroUnserializeT(nint data, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint RetroGetMemoryDataT(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint RetroGetMemorySizeT(uint id);

    private readonly RetroInitT _retroInit;
    private readonly RetroDeinitT _retroDeinit;
    private readonly RetroApiVersionT _retroApiVersion;
    private readonly RetroGetSystemInfoT _retroGetSystemInfo;
    private readonly RetroGetSystemAvInfoT _retroGetSystemAvInfo;
    private readonly RetroSetEnvironmentT _retroSetEnvironment;
    private readonly RetroSetVideoRefreshT _retroSetVideoRefresh;
    private readonly RetroSetAudioSampleT _retroSetAudioSample;
    private readonly RetroSetAudioSampleBatchT _retroSetAudioSampleBatch;
    private readonly RetroSetInputPollT _retroSetInputPoll;
    private readonly RetroSetInputStateT _retroSetInputState;
    private readonly RetroSetControllerPortDeviceT _retroSetControllerPortDevice;
    private readonly RetroResetT _retroReset;
    private readonly RetroRunT _retroRun;
    private readonly RetroLoadGameT _retroLoadGame;
    private readonly RetroUnloadGameT _retroUnloadGame;
    private readonly RetroSerializeSizeT _retroSerializeSize;
    private readonly RetroSerializeT _retroSerialize;
    private readonly RetroUnserializeT _retroUnserialize;
    private readonly RetroGetMemoryDataT _retroGetMemoryData;
    private readonly RetroGetMemorySizeT _retroGetMemorySize;

    // コアに登録したデリゲートはGCされないようインスタンスに保持し続ける
    private RetroEnvironmentCallback? _environmentCb;
    private RetroVideoRefreshCallback? _videoRefreshCb;
    private RetroAudioSampleCallback? _audioSampleCb;
    private RetroAudioSampleBatchCallback? _audioSampleBatchCb;
    private RetroInputPollCallback? _inputPollCb;
    private RetroInputStateCallback? _inputStateCb;

    // retro_load_game に渡した非管理メモリ(ロード中は生存させておく必要がある)
    private nint _loadedPathPtr;
    private nint _loadedDataPtr;
    private string? _extractedTempPath; // zip展開時の一時ファイル(need_fullpathコア用)
    private bool _gameLoaded;
    private bool _disposed;
    private readonly string _corePathForResidencyCheck;

    public LibretroCore(string corePath)
    {
        if (!File.Exists(corePath))
            throw new FileNotFoundException("libretroコアが見つかりません。", corePath);

        _corePathForResidencyCheck = corePath;
        _handle = NativeLibrary.Load(corePath);

        _retroInit = GetDelegate<RetroInitT>("retro_init");
        _retroDeinit = GetDelegate<RetroDeinitT>("retro_deinit");
        _retroApiVersion = GetDelegate<RetroApiVersionT>("retro_api_version");
        _retroGetSystemInfo = GetDelegate<RetroGetSystemInfoT>("retro_get_system_info");
        _retroGetSystemAvInfo = GetDelegate<RetroGetSystemAvInfoT>("retro_get_system_av_info");
        _retroSetEnvironment = GetDelegate<RetroSetEnvironmentT>("retro_set_environment");
        _retroSetVideoRefresh = GetDelegate<RetroSetVideoRefreshT>("retro_set_video_refresh");
        _retroSetAudioSample = GetDelegate<RetroSetAudioSampleT>("retro_set_audio_sample");
        _retroSetAudioSampleBatch = GetDelegate<RetroSetAudioSampleBatchT>("retro_set_audio_sample_batch");
        _retroSetInputPoll = GetDelegate<RetroSetInputPollT>("retro_set_input_poll");
        _retroSetInputState = GetDelegate<RetroSetInputStateT>("retro_set_input_state");
        _retroSetControllerPortDevice = GetDelegate<RetroSetControllerPortDeviceT>("retro_set_controller_port_device");
        _retroReset = GetDelegate<RetroResetT>("retro_reset");
        _retroRun = GetDelegate<RetroRunT>("retro_run");
        _retroLoadGame = GetDelegate<RetroLoadGameT>("retro_load_game");
        _retroUnloadGame = GetDelegate<RetroUnloadGameT>("retro_unload_game");
        _retroSerializeSize = GetDelegate<RetroSerializeSizeT>("retro_serialize_size");
        _retroSerialize = GetDelegate<RetroSerializeT>("retro_serialize");
        _retroUnserialize = GetDelegate<RetroUnserializeT>("retro_unserialize");
        _retroGetMemoryData = GetDelegate<RetroGetMemoryDataT>("retro_get_memory_data");
        _retroGetMemorySize = GetDelegate<RetroGetMemorySizeT>("retro_get_memory_size");
    }

    private T GetDelegate<T>(string exportName) where T : Delegate
    {
        nint proc = NativeLibrary.GetExport(_handle, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(proc);
    }

    public uint ApiVersion => _retroApiVersion();

    public void SetEnvironment(RetroEnvironmentCallback cb)
    {
        _environmentCb = cb; // GC対策で保持
        _retroSetEnvironment(cb);
    }

    public void SetVideoRefresh(RetroVideoRefreshCallback cb)
    {
        _videoRefreshCb = cb;
        _retroSetVideoRefresh(cb);
    }

    public void SetAudioSample(RetroAudioSampleCallback cb)
    {
        _audioSampleCb = cb;
        _retroSetAudioSample(cb);
    }

    public void SetAudioSampleBatch(RetroAudioSampleBatchCallback cb)
    {
        _audioSampleBatchCb = cb;
        _retroSetAudioSampleBatch(cb);
    }

    public void SetInputPoll(RetroInputPollCallback cb)
    {
        _inputPollCb = cb;
        _retroSetInputPoll(cb);
    }

    public void SetInputState(RetroInputStateCallback cb)
    {
        _inputStateCb = cb;
        _retroSetInputState(cb);
    }

    public void Init() => _retroInit();
    public void Deinit() => _retroDeinit();
    public void Reset() => _retroReset();
    public void Run() => _retroRun();
    public void SetControllerPortDevice(uint port, uint device) => _retroSetControllerPortDevice(port, device);

    /// <summary>セーブステートに必要なバイト数を取得する。</summary>
    public int SerializeSize()
    {
        nuint size = _retroSerializeSize();
        // 実用上あり得ない極端な値(コア側の異常値)は0扱いにしておく
        return size > int.MaxValue ? 0 : (int)size;
    }

    /// <summary>現在の内部状態を buffer へ書き出す(retro_serialize)。</summary>
    public bool Serialize(byte[] buffer)
    {
        nint ptr = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            bool ok = _retroSerialize(ptr, (nuint)buffer.Length);
            if (ok) Marshal.Copy(ptr, buffer, 0, buffer.Length);
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>buffer の内容で内部状態を復元する(retro_unserialize)。</summary>
    public bool Unserialize(byte[] buffer)
    {
        nint ptr = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, ptr, buffer.Length);
            return _retroUnserialize(ptr, (nuint)buffer.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>コアが公開している内部メモリ(RetroMemory.*)の先頭ポインタを返す(retro_get_memory_data)。</summary>
    public nint GetMemoryData(uint id) => _retroGetMemoryData(id);

    /// <summary>コアが公開している内部メモリ(RetroMemory.*)のバイト数(retro_get_memory_size)。</summary>
    public int GetMemorySize(uint id)
    {
        nuint size = _retroGetMemorySize(id);
        return size > int.MaxValue ? 0 : (int)size;
    }

    public RetroSystemInfo GetSystemInfo()
    {
        _retroGetSystemInfo(out var raw);
        return new RetroSystemInfo
        {
            LibraryName = Marshal.PtrToStringAnsi(raw.library_name) ?? "(unknown)",
            LibraryVersion = Marshal.PtrToStringAnsi(raw.library_version) ?? "",
            ValidExtensions = Marshal.PtrToStringAnsi(raw.valid_extensions) ?? "",
            NeedFullPath = raw.need_fullpath,
            BlockExtract = raw.block_extract,
        };
    }

    public RetroSystemAvInfo GetSystemAvInfo()
    {
        _retroGetSystemAvInfo(out var info);
        return info;
    }

    /// <summary>ROMファイルをロードする。</summary>
    public bool LoadGame(string romPath)
    {
        // ゲーム無し起動(本体/BIOSのみ)。
        if (string.IsNullOrEmpty(romPath))
        {
            FreeLoadedGameMemory();
            _gameLoaded = _retroLoadGame(nint.Zero);
            return _gameLoaded;
        }

        if (!File.Exists(romPath))
            throw new FileNotFoundException("ROMファイルが見つかりません。", romPath);

        var info = GetSystemInfo();

        FreeLoadedGameMemory();

        // zipに加えて、7zも(rdblist.jsonの拡張子に明示した機種で渡されてくる)展開対象にする。
        bool isZip = ArchiveReader.IsSupported(romPath);

        string effectivePath = romPath;
        byte[]? extractedBytes = null;
        string? extractedInnerName = null;

        if (isZip && !info.BlockExtract)
        {
            extractedBytes = ExtractSingleRomFromArchive(romPath, info.ValidExtensions, out string innerName);
            extractedInnerName = innerName;

            if (info.NeedFullPath)
            {
                // フルパスを要求するコア用に、一時ファイルへ展開してそのパスを渡す。
                string tempDir = Path.Combine(Path.GetTempPath(), "EmulatorLauncher_extract");
                Directory.CreateDirectory(tempDir);
                _extractedTempPath = Path.Combine(tempDir, innerName);
                File.WriteAllBytes(_extractedTempPath, extractedBytes);
                effectivePath = _extractedTempPath;
                extractedBytes = null; // フルパスモードでは使わない
            }
        }

        RetroGameInfoRaw gameInfo;

        if (info.NeedFullPath)
        {
            _loadedPathPtr = Marshal.StringToHGlobalAnsi(effectivePath);

            gameInfo = new RetroGameInfoRaw
            {
                path = _loadedPathPtr,
                data = nint.Zero,
                size = 0,
                meta = nint.Zero
            };
        }
        else
        {
            // ★ メモリ渡しモード（mgba, gambatte, genesis-plus-gx など）
            byte[] bytes = extractedBytes ?? File.ReadAllBytes(romPath);
            _loadedDataPtr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, _loadedDataPtr, bytes.Length);

            // pathも渡しておく(セーブファイル名の導出等にpathを参照するコアがあるため。元のROMパス(zipなら展開前のzip自体のパス)を渡す) zipから展開した場合は、zip内のROMファイル名(拡張子が.gba等)を持つパスを渡す。
            string reportedPath = romPath;
            if (extractedBytes != null && !string.IsNullOrEmpty(extractedInnerName))
                reportedPath = Path.Combine(Path.GetDirectoryName(romPath) ?? "", extractedInnerName);
            _loadedPathPtr = Marshal.StringToHGlobalAnsi(reportedPath);

            gameInfo = new RetroGameInfoRaw
            {
                path = _loadedPathPtr,
                data = _loadedDataPtr,
                size = (nuint)bytes.Length,
                meta = nint.Zero
            };
        }

        nint gameInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<RetroGameInfoRaw>());
        try
        {
            Marshal.StructureToPtr(gameInfo, gameInfoPtr, false);
            _gameLoaded = _retroLoadGame(gameInfoPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(gameInfoPtr);
        }

        if (!_gameLoaded)
            FreeLoadedGameMemory();

        return _gameLoaded;
    }

    /// <summary>zip/7z内から、コアの valid_extensions("bin|smc|sfc"のような'|'区切り)に一致する最初のエントリを取り出す。</summary>
    private static byte[] ExtractSingleRomFromArchive(string archivePath, string validExtensions, out string innerFileName)
    {
        var validExts = validExtensions
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => "." + e.Trim().ToLowerInvariant())
            .ToHashSet();

        var entries = ArchiveReader.ListEntries(archivePath);

        var entry = entries.FirstOrDefault(e =>
                validExts.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()))
            ?? entries.FirstOrDefault();

        if (entry == null)
            throw new InvalidOperationException($"アーカイブ内に有効なROMファイルが見つかりません: {archivePath}");

        innerFileName = Path.GetFileName(entry.FullName);
        return ArchiveReader.ReadEntryBytes(archivePath, entry.FullName);
    }


    public void UnloadGame()
    {
        if (_gameLoaded)
        {
            _retroUnloadGame();
            _gameLoaded = false;
        }
        FreeLoadedGameMemory();
    }

    private void FreeLoadedGameMemory()
    {
        if (_loadedPathPtr != nint.Zero)
        {
            Marshal.FreeHGlobal(_loadedPathPtr);
            _loadedPathPtr = nint.Zero;
        }
        if (_loadedDataPtr != nint.Zero)
        {
            Marshal.FreeHGlobal(_loadedDataPtr);
            _loadedDataPtr = nint.Zero;
        }
        if (_extractedTempPath != null)
        {
            try { File.Delete(_extractedTempPath); } catch { /* 後始末なので握りつぶす */ }
            _extractedTempPath = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        FreeLoadedGameMemory();

        // DolphinのようにネイティブスレッドやDLLMain/静的デストラクタを多用するコアは、FreeLibrary(アンロード)後に、DLLが中途半端にロードされたまま残る/ローダーロックを保持したまま止まる、といった状態になることがある。
        if (_handle != nint.Zero && !IsResidentCore(_corePathForResidencyCheck))
            NativeLibrary.Free(_handle);
    }

    /// <summary>アンロードせずプロセス終了まで常駐させるコア(ファイル名の先頭一致)。</summary>
    private static readonly string[] ResidentCorePrefixes = { "dolphin_libretro" };

    private static bool IsResidentCore(string corePath)
    {
        string name = Path.GetFileName(corePath);
        foreach (var prefix in ResidentCorePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}