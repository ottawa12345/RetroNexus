using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EmulatorLauncher.Emulation;

/// <summary>EmulationHostのうち、セーブステート(手動スロット/オートセーブ)・永続メモリ(SRAM等)の読み書きを担当する部分。</summary>
public sealed partial class EmulationHost
{
    // ステートセーブ手動の「どこでもセーブ」は5枠(フォルダ 1〜5)。
    public const int MaxSaveSlots = 4;

    // オートセーブの間隔。
    public const int AutoSaveIntervalMinutes = 5;
    private const double AutoSaveIntervalMs = AutoSaveIntervalMinutes * 60_000.0;
    private double _autoSaveElapsedMs;

    /// <summary>オートセーブを行うかどうか(「設定」画面のトグルに対応)。</summary>
    public bool AutoSaveEnabled
    {
        get => _autoSaveEnabled;
        set => _autoSaveEnabled = value;
    }
    private volatile bool _autoSaveEnabled = true;

    // UIスレッドから要求され、エミュレーションスレッドのループ内で処理される。
    private volatile int _pendingSaveSlot;
    private volatile int _pendingLoadSlot;

    // オートセーブ枠(auto)のロード要求。
    private volatile bool _pendingLoadAutoSave;

    // 「コア再起動用」の一時ステート(restartフォルダ)。
    private volatile bool _pendingSaveRestart;
    private volatile bool _pendingLoadRestart;
    private int _framesRunTotal; // retro_runを実行した回数(再起動ステートのロードを数フレーム後にするため)
    private const int RestartLoadMinFrames = 2;
    private const string RestartMetadataKey = "restart";

    /// <summary>再起動用ステートの保存が終わったときに発生する(引数=成功したか)。</summary>
    public event EventHandler<bool>? RestartStateSaved;

    /// <summary>再起動用ステートの読み込みが終わったときに発生する(引数=成功したか)。</summary>
    public event EventHandler<bool>? RestartStateLoaded;

    /// <summary>次のループで、再起動用ステートへ保存する(一時停止中でも処理される)。</summary>
    public void RequestSaveRestartState() => _pendingSaveRestart = true;

    /// <summary>起動後、数フレーム回ってから、再起動用ステートを読み込む(Start()より前に呼んでよい)。</summary>
    public void RequestLoadRestartState() => _pendingLoadRestart = true;

    private string GetRestartFolder() => Path.Combine(GetGameSaveFolder(), RestartMetadataKey);

    // 「タイトル選択にもどる」で終了するときの再開用ステート(suspendフォルダ。SuspendState参照)。
    private volatile bool _pendingSaveSuspend;
    private volatile bool _pendingLoadSuspend;

    /// <summary>再開用ステートの保存が終わったときに発生する(引数=成功したか)。</summary>
    public event EventHandler<bool>? SuspendStateSaved;

    /// <summary>再開用ステートの読み込みが終わったときに発生する(引数=成功したか)。</summary>
    public event EventHandler<bool>? SuspendStateLoaded;

    /// <summary>次のループで、再開用(suspend)ステートへ保存する(一時停止中でも処理される)。</summary>
    public void RequestSaveSuspendState() => _pendingSaveSuspend = true;

    /// <summary>起動後、数フレーム回ってから、再開用(suspend)ステートを読み込む(Start()より前に呼んでよい)。</summary>
    public void RequestLoadSuspendState() => _pendingLoadSuspend = true;

    private string GetSuspendFolder() => Path.Combine(GetGameSaveFolder(), SuspendState.FolderName);

    // 起動後に読み込む再開用ステートのフォルダ名。
    private string _resumeLoadKey = SuspendState.FolderName;

    /// <summary>起動後、数フレーム回ってから、指定したステート(手動スロット番号 または "auto")を読み込む(Start()より前に呼んでよい)。</summary>
    public void RequestLoadStateOnStart(string key)
    {
        bool valid = key == AutoSaveMetadataKey || (key.Length is > 0 and <= 2 && key.All(char.IsAsciiDigit));
        if (!valid) return;
        _resumeLoadKey = key;
        _pendingLoadSuspend = true;
    }

    private void SaveSuspendInternal()
    {
        var (success, message) = SaveStateCore(GetSuspendFolder(), SuspendState.FolderName);
        ReportStatus(success ? "再開用に保存しました" : $"再開用の保存に失敗: {message}");
        System.Diagnostics.Debug.WriteLine($"[EmulationHost] 再開用ステート保存: success={success} message={message} folder={GetSuspendFolder()}");
        SuspendStateSaved?.Invoke(this, success);
    }

    // 起動直後は、コアによってはretro_unserializeが失敗したり反映されなかったりすることがあるため、失敗したら少し間を置いて数回やり直す(最初の試行はRestartLoadMinFramesフレーム後)。
    private const int LoadRetryMaxAttempts = 6;
    private const int LoadRetryIntervalFrames = 30;
    private int _suspendLoadAttempts;
    private int _suspendNextAttemptFrame = RestartLoadMinFrames;

    /// <summary>再開用ステートを読み込む。</summary>
    private bool LoadSuspendInternal()
    {
        _suspendLoadAttempts++;
        string loadFolder = Path.Combine(GetGameSaveFolder(), _resumeLoadKey);
        var (success, message) = LoadStateCore(loadFolder);
        System.Diagnostics.Debug.WriteLine($"[EmulationHost] 再開用ステート読込: success={success} message={message} folder={loadFolder} frames={_framesRunTotal} attempt={_suspendLoadAttempts}");

        if (!success && _suspendLoadAttempts < LoadRetryMaxAttempts)
        {
            _suspendNextAttemptFrame = _framesRunTotal + LoadRetryIntervalFrames;
            return false;
        }

        ReportStatus(success ? "続きから再開しました" : $"再開に失敗: {message}");
        SuspendStateLoaded?.Invoke(this, success);
        return true;
    }

    private void SaveRestartInternal()
    {
        var (success, message) = SaveStateCore(GetRestartFolder(), RestartMetadataKey);
        ReportStatus(success ? "再起動用に保存しました" : $"再起動用の保存に失敗: {message}");
        RestartStateSaved?.Invoke(this, success);
    }

    private int _restartLoadAttempts;
    private int _restartNextAttemptFrame = RestartLoadMinFrames;

    /// <summary>再起動用ステートを読み込む。</summary>
    private bool LoadRestartInternal()
    {
        _restartLoadAttempts++;
        var (success, message) = LoadStateCore(GetRestartFolder());
        System.Diagnostics.Debug.WriteLine($"[EmulationHost] 再起動用ステート読込: success={success} message={message} frames={_framesRunTotal} attempt={_restartLoadAttempts}");

        if (!success && _restartLoadAttempts < LoadRetryMaxAttempts)
        {
            _restartNextAttemptFrame = _framesRunTotal + LoadRetryIntervalFrames;
            return false;
        }

        ReportStatus(success ? "続きから再開しました" : $"再開に失敗: {message}");
        RestartStateLoaded?.Invoke(this, success);
        return true;
    }

    /// <summary>セーブ/ロード操作が完了した際にUIスレッドへ通知するためのイベント引数。</summary>
    public sealed class StateOperationEventArgs : EventArgs
    {
        public required bool IsSave { get; init; }
        public required int Slot { get; init; }
        public required bool Success { get; init; }
        public required string Message { get; init; }

        /// <summary>オートセーブ枠(auto)に対する操作の場合 true。</summary>
        public bool IsAutoSave { get; init; }
    }

    /// <summary>セーブ/ロードが完了した(成功/失敗いずれも)ときに発生する。</summary>
    public event EventHandler<StateOperationEventArgs>? StateOperationCompleted;

    // ステートセーブ

    /// <summary>次にエミュレーションスレッドのループが回ったタイミングで、指定スロットへセーブする。</summary>
    public void RequestSaveState(int slot)
    {
        if (slot is < 1 or > MaxSaveSlots) return;
        _pendingSaveSlot = slot;
    }

    /// <summary>次にエミュレーションスレッドのループが回ったタイミングで、指定スロットからロードする。</summary>
    public void RequestLoadState(int slot)
    {
        if (slot is < 1 or > MaxSaveSlots) return;
        _pendingLoadSlot = slot;
    }

    /// <summary>次にエミュレーションスレッドのループが回ったタイミングで、オートセーブ枠(auto)からロードする。</summary>
    public void RequestLoadAutoSave() => _pendingLoadAutoSave = true;

    /// <summary>statesaveフォルダから読み取った、セーブスロット1件分の生データ。</summary>
    public sealed class SaveSlotEntry
    {
        /// <summary>手動スロットの場合は1〜MaxSaveSlot。</summary>
        public required int Slot { get; init; }

        /// <summary>オートセーブ枠(auto フォルダ)を表す場合 true。</summary>
        public bool IsAutoSave { get; init; }

        public required bool HasData { get; init; }
        public DateTime? SavedAt { get; init; }
        public string? ScreenshotPath { get; init; }
    }

    /// <summary>実行中のコア/ROMに対応する statesave フォルダから、1〜MaxSaveSlots 分(手動セーブのみ、オートセーブ枠は含まない)の状態を読み取って返す。</summary>
    public IReadOnlyList<SaveSlotEntry> GetSaveSlots()
    {
        var result = new List<SaveSlotEntry>(MaxSaveSlots);

        var metadata = LoadSaveMetadataOrEmpty();

        for (int slot = 1; slot <= MaxSaveSlots; slot++)
        {
            string slotFolder = GetSlotFolder(slot);
            string stateFile = Path.Combine(slotFolder, "state.dat");
            string screenshotFile = Path.Combine(slotFolder, "screenshot.bmp");

            bool hasData = File.Exists(stateFile);
            DateTime? savedAt = null;

            if (hasData
                && metadata.Slots.TryGetValue(slot.ToString(), out var slotMeta)
                && DateTime.TryParse(slotMeta.SavedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                savedAt = parsed;
            }

            result.Add(new SaveSlotEntry
            {
                Slot = slot,
                IsAutoSave = false,
                HasData = hasData,
                SavedAt = savedAt,
                ScreenshotPath = hasData && File.Exists(screenshotFile) ? screenshotFile : null,
            });
        }

        return result;
    }

    /// <summary>オートセーブ枠(auto フォルダ)の状態を返す。</summary>
    public SaveSlotEntry GetAutoSaveSlotEntry()
    {
        var metadata = LoadSaveMetadataOrEmpty();

        string folder = GetAutoSaveFolder();
        string stateFile = Path.Combine(folder, "state.dat");
        string screenshotFile = Path.Combine(folder, "screenshot.bmp");

        bool hasData = File.Exists(stateFile);
        DateTime? savedAt = null;

        if (hasData
            && metadata.Slots.TryGetValue(AutoSaveMetadataKey, out var slotMeta)
            && DateTime.TryParse(slotMeta.SavedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            savedAt = parsed;
        }

        return new SaveSlotEntry
        {
            Slot = 0,
            IsAutoSave = true,
            HasData = hasData,
            SavedAt = savedAt,
            ScreenshotPath = hasData && File.Exists(screenshotFile) ? screenshotFile : null,
        };
    }

    // statesave フォルダ構成・メタデータのヘルパー例: <実行ファイルのディレクトリ>/statesave/<コアファイル名>/<ROMファイル名>/<スロット番号 or "auto">/ state.dat … retro_serialize() の生データscreenshot.bmp … 保存時点のスクリーンショット(無圧縮24bit BMP) <ROMファイル名>/saves.json … 各スロット/オートセーブの保存日時などのメタデータ

    private const string AutoSaveMetadataKey = "auto";

    private string GetGameSaveFolder()
    {
        string coreFolder = SanitizeForPath(Path.GetFileNameWithoutExtension(_corePath));
        string gameFolder = SanitizeForPath(string.IsNullOrEmpty(_romPath) ? "_bios" : Path.GetFileNameWithoutExtension(_romPath));
        return Path.Combine(AppContext.BaseDirectory, "statesave", coreFolder, gameFolder);
    }

    private string GetSlotFolder(int slot) => Path.Combine(GetGameSaveFolder(), slot.ToString());

    private string GetAutoSaveFolder() => Path.Combine(GetGameSaveFolder(), AutoSaveMetadataKey);

    private string GetSaveMetadataPath() => Path.Combine(GetGameSaveFolder(), "saves.json");

    private sealed class SaveMetadata
    {
        public Dictionary<string, SaveSlotMetadata> Slots { get; set; } = new();
    }

    private sealed class SaveSlotMetadata
    {
        public string SavedAt { get; set; } = "";

        /// <summary>保存時に画面へ出していた比率(幅/高さ)。</summary>
        public double Aspect { get; set; }
    }

    /// <summary>ステートを保存するとき、いま画面に出している比率(幅/高さ)を返す関数。</summary>
    public Func<double>? DisplayAspectProvider { get; set; }

    private SaveMetadata LoadSaveMetadataOrEmpty()
    {
        try
        {
            string path = GetSaveMetadataPath();
            if (!File.Exists(path)) return new SaveMetadata();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SaveMetadata>(json) ?? new SaveMetadata();
        }
        catch
        {
            // 壊れたjson等は「メタデータ無し」として扱う(実データ自体はstate.datの有無で判定するため一覧表示自体は継続できる)
            return new SaveMetadata();
        }
    }

    private void UpdateSaveMetadata(string metadataKey, DateTime savedAt)
    {
        var metadata = LoadSaveMetadataOrEmpty();
        double aspect = 0;
        try { aspect = DisplayAspectProvider?.Invoke() ?? 0; } catch { aspect = 0; }
        metadata.Slots[metadataKey] = new SaveSlotMetadata { SavedAt = savedAt.ToString("o"), Aspect = aspect > 0 ? aspect : 0 };

        string folder = GetGameSaveFolder();
        Directory.CreateDirectory(folder);

        string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetSaveMetadataPath(), json);
    }

    /// <summary>セーブ処理の本体(共通)。</summary>
    private (bool Success, string Message) SaveStateCore(string folder, string metadataKey)
    {
        try
        {
            if (_core is null)
                return (false, "コアが読み込まれていません");

            int size = _core.SerializeSize();
            if (size <= 0)
                return (false, "このコアはステートセーブに対応していません");

            var buffer = new byte[size];
            if (!_core.Serialize(buffer))
                return (false, "retro_serializeが失敗しました");

            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "state.dat"), buffer);

            // DS/3DSは、一時停止直前に「上画面のみ」で撮った画像(_stateThumbBgra)があればそれを使う。
            if (_isHwRendered && _hwFbo != 0 && !_stateThumbValid)
                CaptureHwThumbnail(_lastHwFrameWidth, _lastHwFrameHeight);

            if (_stateThumbValid)
            {
                try
                {
                    SaveBmp(Path.Combine(folder, "screenshot.bmp"), _stateThumbBgra, _stateThumbW, _stateThumbH);
                }
                catch
                {
                    // スクリーンショットの保存に失敗しても、セーブデータ自体は有効なので握りつぶす
                }
            }
            else if (_rawThumbValid)
            {
                try
                {
                    SaveBmp(Path.Combine(folder, "screenshot.bmp"), _rawThumbBgra, _rawThumbWidth, _rawThumbHeight);
                }
                catch
                {
                    // スクリーンショットの保存に失敗しても、セーブデータ自体は有効なので握りつぶす
                }
            }

            UpdateSaveMetadata(metadataKey, DateTime.Now);
            return (true, "セーブしました");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>ロード処理の本体(共通)。</summary>
    private (bool Success, string Message) LoadStateCore(string folder)
    {
        try
        {
            string stateFile = Path.Combine(folder, "state.dat");
            if (!File.Exists(stateFile))
                return (false, "セーブデータがありません");

            if (_core is null)
                return (false, "コアが読み込まれていません");

            byte[] buffer = File.ReadAllBytes(stateFile);
            if (!_core.Unserialize(buffer))
                return (false, "retro_unserializeが失敗しました");

            return (true, "ロードしました");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>手動スロットへのセーブ。</summary>
    private void SaveStateInternal(int slot)
    {
        var (success, message) = SaveStateCore(GetSlotFolder(slot), slot.ToString());
        ReportStatus(success ? $"セーブしました (スロット{slot})" : $"セーブ失敗: {message}");
        RaiseStateOperationCompleted(isSave: true, slot, success, message);
    }

    /// <summary>手動スロットからのロード。</summary>
    private void LoadStateInternal(int slot)
    {
        var (success, message) = LoadStateCore(GetSlotFolder(slot));
        ReportStatus(success ? $"ロードしました (スロット{slot})" : $"ロード失敗: {message}");
        RaiseStateOperationCompleted(isSave: false, slot, success, message);
    }

    /// <summary>オートセーブ枠(auto)へのセーブ。</summary>
    private void AutoSaveInternal()
    {
        var (success, message) = SaveStateCore(GetAutoSaveFolder(), AutoSaveMetadataKey);
        ReportStatus(success ? "オートセーブしました" : $"オートセーブ失敗: {message}");
        RaiseStateOperationCompleted(isSave: true, slot: 0, success, message, isAutoSave: true);
    }

    /// <summary>オートセーブ枠(auto)からのロード。</summary>
    private void LoadAutoSaveInternal()
    {
        var (success, message) = LoadStateCore(GetAutoSaveFolder());
        ReportStatus(success ? "オートセーブをロードしました" : $"ロード失敗: {message}");
        RaiseStateOperationCompleted(isSave: false, slot: 0, success, message, isAutoSave: true);
    }

    // バッテリーバックアップ(SRAM/RTC)=ゲーム内セーブステートセーブ(retro_serialize)とは別物で、カートリッジのバッテリーバックアップRAMに相当する領域を retro_get_memory_data で直接読み書きする。

    private static readonly (uint Id, string Extension)[] PersistentMemoryTypes =
    {
        (RetroMemory.SAVE_RAM, ".srm"),
        (RetroMemory.RTC, ".rtc"),
    };

    // 読み込みが正常に済んだ(またはファイルが無いことを確認できた)メモリ種別だけを書き戻し対象にする。
    private readonly HashSet<uint> _persistentMemoryReady = new();

    // 最後にファイルと一致していたメモリ内容。
    private readonly Dictionary<uint, byte[]> _persistentMemoryLastSynced = new();

    private string GetPersistentMemoryPath(string extension)
    {
        string romName = SanitizeForPath(string.IsNullOrEmpty(_romPath) ? "_bios" : Path.GetFileNameWithoutExtension(_romPath));
        return Path.Combine(GetCoreSaveDirectory(), romName + extension);
    }

    /// <summary>セーブファイルをコアのメモリへ読み込む。</summary>
    private void LoadPersistentMemory()
    {
        if (_core is null) return;

        foreach (var (id, ext) in PersistentMemoryTypes)
        {
            int size = _core.GetMemorySize(id);
            nint ptr = _core.GetMemoryData(id);
            if (size <= 0 || ptr == nint.Zero) continue; // このコア/ゲームは該当メモリを持たない

            string path = GetPersistentMemoryPath(ext);
            try
            {
                if (File.Exists(path))
                {
                    byte[] data = File.ReadAllBytes(path);
                    Marshal.Copy(data, 0, ptr, Math.Min(data.Length, size));

                    if (data.Length != size)
                        ReportStatus($"警告: {Path.GetFileName(path)} のサイズ({data.Length})がコアの想定({size})と異なります");
                    else
                        ReportStatus($"セーブデータを読み込みました: {Path.GetFileName(path)}");
                }

                var snapshot = new byte[size];
                Marshal.Copy(ptr, snapshot, 0, size);
                _persistentMemoryLastSynced[id] = snapshot;
                _persistentMemoryReady.Add(id);
            }
            catch (Exception ex)
            {
                ReportStatus($"セーブデータの読み込みに失敗しました(今回は上書き保存しません): {ex.Message}");
            }
        }
    }

    /// <summary>コアのメモリ内容をセーブファイルへ書き出す(前回から変化がある場合のみ)。</summary>
    private void SavePersistentMemory()
    {
        if (_core is null) return;

        foreach (var (id, ext) in PersistentMemoryTypes)
        {
            if (!_persistentMemoryReady.Contains(id)) continue;

            int size = _core.GetMemorySize(id);
            nint ptr = _core.GetMemoryData(id);
            if (size <= 0 || ptr == nint.Zero) continue;

            var current = new byte[size];
            Marshal.Copy(ptr, current, 0, size);

            if (_persistentMemoryLastSynced.TryGetValue(id, out var last)
                && last.AsSpan().SequenceEqual(current))
                continue;

            string path = GetPersistentMemoryPath(ext);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // 書き込み途中で落ちてもセーブが壊れないよう、一時ファイルに書いてから置き換える
                string tempPath = path + ".tmp";
                File.WriteAllBytes(tempPath, current);
                File.Move(tempPath, path, overwrite: true);

                _persistentMemoryLastSynced[id] = current;
                ReportStatus($"セーブデータを保存しました: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                ReportStatus($"セーブデータの保存に失敗しました: {ex.Message}");
            }
        }
    }

    private void RaiseStateOperationCompleted(bool isSave, int slot, bool success, string message, bool isAutoSave = false)
    {
        StateOperationCompleted?.Invoke(this, new StateOperationEventArgs
        {
            IsSave = isSave,
            Slot = slot,
            Success = success,
            Message = message,
            IsAutoSave = isAutoSave,
        });
    }

    /// <summary>BGRA8のピクセル列を、無圧縮24bit BMPとしてそのまま書き出す。</summary>
    private static void SaveBmp(string path, byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        int rowSize = (width * 3 + 3) / 4 * 4; // 4バイト境界にパディング
        int imageSize = rowSize * height;
        int fileSize = 54 + imageSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((short)0);
        bw.Write((short)0);
        bw.Write(54); // ピクセルデータの開始オフセット

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40);
        bw.Write(width);
        bw.Write(height); // 正値 = ボトムアップ(BMPの標準的な並び)
        bw.Write((short)1); // planes
        bw.Write((short)24); // bpp
        bw.Write(0); // BI_RGB(無圧縮)
        bw.Write(imageSize);
        bw.Write(2835); bw.Write(2835); // 解像度(96dpi相当、実質未使用)
        bw.Write(0); bw.Write(0); // パレット無し

        byte[] row = new byte[rowSize];
        for (int y = height - 1; y >= 0; y--) // BMPは下段から書く
        {
            int srcRowBase = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int si = srcRowBase + x * 4;
                int di = x * 3;
                row[di + 0] = bgra[si + 0]; // B
                row[di + 1] = bgra[si + 1]; // G
                row[di + 2] = bgra[si + 2]; // R
            }
            bw.Write(row);
        }
    }

}
