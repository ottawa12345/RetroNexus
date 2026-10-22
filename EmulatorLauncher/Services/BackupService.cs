using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.Storage;

namespace EmulatorLauncher.Services;

/// <summary>1つのバックアップ項目(基本データ/ステート/画像)と、その容量。</summary>
public sealed record BackupPart(string Id, string Label, long Bytes);

/// <summary>バックアップのzipファイル1つの情報(中のbackup.jsonから読む)。</summary>
public sealed record BackupInfo(string Path, DateTime Created, long SizeBytes, IReadOnlyList<string> Parts, string AppVersion);

/// <summary>
/// データのバックアップと復元。
///
/// バックアップは、項目を選んで、保存先フォルダに RetroNexus_yyyyMMdd_HHmmss.zip を1つ作る。項目は次の3つ。
/// <list type="bullet">
/// <item>基本データ(必須): ゲームの登録(DB)、設定、コントローラ設定、ランチャー/コンソール/コアの設定、コア毎の設定、プラグイン、ゲーム内セーブ(saves)。</item>
/// <item>ステート: ステートセーブ・再開データ(statesave)。大きくなりやすいので、選べる。</item>
/// <item>システム画像: コレクションのアイコン、利用者が置いた画像(Images)、ゲーム情報の編集で選んだ表紙(各ROMフォルダのNamed_Boxartsにある custom_ で始まるファイル)。
    /// 自動で取得したパッケージ画像は含めない(再取得できるため)。</item>
/// </list>
/// 含めないもの: ROM本体、BIOS(system)、コアのDLL、ダウンロードして再取得できるデータ(rdb/jdb/arcade)、ログ。
///
/// 復元は「次回の起動時」に行う(DBや設定ファイルをアプリが開いている間に差し替えない/終了時の書き込みで戻されないため)。
/// 復元を頼むと、まず今の状態を「復元前」のバックアップとして保存し、復元の予約(restore_pending.json)を書く。
/// アプリが次に起動したとき、App.OnLaunched の最初に <see cref="ApplyPendingRestore"/> が予約どおりにファイルを上書きする
/// (zipにあるファイルを上書きするだけで、zipに無いファイルは消さない)。
/// </summary>
public static class BackupService
{
    public const string BasePartId = "base";
    public const string StatesPartId = "states";
    public const string ImagesPartId = "images";

    private const string ManifestName = "backup.json";

    /// <summary>backup.json の書式のバージョン。読み込めない(これより新しい)バックアップは、一覧に出さず、復元しない。</summary>
    public const int FormatVersion = 1;

    private const string FormatName = "RetroNexus-Backup";
    private const string DbFileName = "EmulatorLauncher.db";

    /// <summary>data\ の中で、バックアップに含めないフォルダ(再取得できる/ログ/別の項目に含めるもの)。</summary>
    private static readonly string[] DataExcludedFolders = { "log", "rdb", "jdb", "arcade", "collection_icons" };

    private static string AppDir => AppContext.BaseDirectory;
    private static string DataDir => Path.Combine(AppDir, "data");
    private static string PendingPath => Path.Combine(AppDir, "restore_pending.json");

    private static string? _restoreResult;

    // ---- 保存先 ----

    /// <summary>バックアップの保存先。設定が空なら、実行フォルダの backup。</summary>
    public static string GetFolder()
    {
        string configured = AppPreferencesStore.Load().BackupFolder;
        return string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppDir, "backup") : configured;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0.#") + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.#") + " MB";
        return (mb / 1024.0).ToString("0.##") + " GB";
    }

    public static string PartLabel(string id) => id switch
    {
        BasePartId => "基本データ",
        StatesPartId => "ステート",
        ImagesPartId => "システム画像",
        _ => id,
    };

    // ---- 対象ファイルの列挙 ----

    private sealed record FileItem(string FullPath, string EntryName);
    private sealed record BoxArt(string EntryName, string FullPath, string Root);

    private static string Rel(string fullPath)
        => Path.GetRelativePath(AppDir, fullPath).Replace('\\', '/');

    private static IEnumerable<string> SafeFiles(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList(); }
        catch { yield break; }
        foreach (string f in files) yield return f;
    }

    private static IEnumerable<FileItem> BaseFiles()
    {
        // data\ 直下のファイル(設定のjsonなど)。DBは別に、整合の取れた複製を作って入れる。
        if (Directory.Exists(DataDir))
        {
            IEnumerable<string> top;
            try { top = Directory.EnumerateFiles(DataDir).ToList(); } catch { top = Array.Empty<string>(); }
            foreach (string f in top)
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith(DbFileName, StringComparison.OrdinalIgnoreCase)) continue;   // .db / -wal / -shm
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new FileItem(f, BasePartId + "/" + Rel(f));
            }

            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(DataDir).ToList(); } catch { subs = Array.Empty<string>(); }
            foreach (string d in subs)
            {
                string name = Path.GetFileName(d);
                if (DataExcludedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (string f in SafeFiles(d)) yield return new FileItem(f, BasePartId + "/" + Rel(f));
            }
        }

        foreach (string folder in new[] { "config", "Plugins", "saves" })
            foreach (string f in SafeFiles(Path.Combine(AppDir, folder)))
                yield return new FileItem(f, BasePartId + "/" + Rel(f));
    }

    private static IEnumerable<FileItem> StateFiles()
    {
        foreach (string f in SafeFiles(Path.Combine(AppDir, "statesave")))
            yield return new FileItem(f, StatesPartId + "/" + Rel(f));
    }

    private static IEnumerable<FileItem> ImageFiles()
    {
        foreach (string f in SafeFiles(Path.Combine(DataDir, "collection_icons")))
            yield return new FileItem(f, ImagesPartId + "/" + Rel(f));
        foreach (string f in SafeFiles(Path.Combine(AppDir, "Images")))
            yield return new FileItem(f, ImagesPartId + "/" + Rel(f));
    }

    /// <summary>ゲーム情報の編集で選んだ表紙(各ROMフォルダの RetroNexus\Named_Boxarts\custom_*)。</summary>
    private static async Task<List<BoxArt>> FindCustomBoxArtsAsync()
    {
        var result = new List<BoxArt>();
        IReadOnlyList<EmulatorLauncher.Models.RomFolder> folders;
        try { folders = await RomFolderRepository.GetAllAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            AppLog.Warn("Backup", "ROMフォルダの一覧を取得できないため、表紙の画像を含めません", ex);
            return result;
        }

        foreach (EmulatorLauncher.Models.RomFolder folder in folders)
        {
            string dir = Path.Combine(folder.Path, "RetroNexus", "Named_Boxarts");
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir, "custom_*"); } catch { continue; }
            foreach (string f in files)
                result.Add(new BoxArt($"{ImagesPartId}/boxarts/{folder.No}/{Path.GetFileName(f)}", f, folder.Path));
        }
        return result;
    }

    private static long Sum(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (string p in paths)
        {
            try { total += new FileInfo(p).Length; } catch { /* 読めないものは数えない */ }
        }
        return total;
    }

    /// <summary>項目ごとの容量(圧縮前の合計)を調べる。</summary>
    public static async Task<IReadOnlyList<BackupPart>> MeasureAsync()
    {
        List<BoxArt> boxArts = await FindCustomBoxArtsAsync().ConfigureAwait(false);
        return await Task.Run(() =>
        {
            long db = Sum(new[] { Path.Combine(DataDir, DbFileName), Path.Combine(DataDir, DbFileName + "-wal") });
            long basePart = db + Sum(BaseFiles().Select(i => i.FullPath));
            long states = Sum(StateFiles().Select(i => i.FullPath));
            long images = Sum(ImageFiles().Select(i => i.FullPath)) + Sum(boxArts.Select(b => b.FullPath));
            return (IReadOnlyList<BackupPart>)new List<BackupPart>
            {
                new(BasePartId, PartLabel(BasePartId), basePart),
                new(StatesPartId, PartLabel(StatesPartId), states),
                new(ImagesPartId, PartLabel(ImagesPartId), images),
            };
        }).ConfigureAwait(false);
    }

    // ---- バックアップの作成 ----

    /// <summary>選んだ項目をzipにして保存する。保存したファイルのパスを返す。基本データは必ず含める。</summary>
    public static async Task<string> CreateAsync(IEnumerable<string> parts, string namePrefix = "RetroNexus")
    {
        var selected = new HashSet<string>(parts ?? Array.Empty<string>(), StringComparer.Ordinal) { BasePartId };

        string folder = GetFolder();
        Directory.CreateDirectory(folder);
        string zipPath = Path.Combine(folder, $"{namePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        string tempZip = zipPath + ".tmp";

        // DBは、使用中でも整合の取れた複製を作ってから入れる(SQLiteのバックアップ機能)。
        string? dbCopy = null;
        try
        {
            dbCopy = Path.Combine(Path.GetTempPath(), "retronexus_backup_" + Guid.NewGuid().ToString("N") + ".db");
            await DatabaseManager.BackupToAsync(dbCopy).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Backup", "DBの複製を作れませんでした", ex);
            dbCopy = null;
        }

        List<BoxArt> boxArts = selected.Contains(ImagesPartId) ? await FindCustomBoxArtsAsync().ConfigureAwait(false) : new List<BoxArt>();
        string appVersion = GetAppVersion();
        var stats = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);

        try
        {
            await Task.Run(() =>
            {
                int failed = 0;
                using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    if (dbCopy is not null && File.Exists(dbCopy))
                        AddFile(zip, dbCopy, $"{BasePartId}/data/{DbFileName}", stats, ref failed);
                    else
                        failed++;

                    IEnumerable<FileItem> items = BaseFiles();
                    if (selected.Contains(StatesPartId)) items = items.Concat(StateFiles());
                    if (selected.Contains(ImagesPartId)) items = items.Concat(ImageFiles());
                    foreach (FileItem item in items) AddFile(zip, item.FullPath, item.EntryName, stats, ref failed);
                    foreach (BoxArt b in boxArts) AddFile(zip, b.FullPath, b.EntryName, stats, ref failed);

                    // バックアップの情報(書式のバージョン、作成日時、アプリの版、項目ごとのファイル数と容量など)。
                    var manifest = new
                    {
                        format = FormatName,
                        formatVersion = FormatVersion,
                        createdAt = DateTime.Now.ToString("o"),
                        appName = "RetroNexus",
                        appVersion,
                        machineName = Environment.MachineName,
                        osVersion = Environment.OSVersion.VersionString,
                        parts = selected.ToArray(),
                        partDetails = selected.Select(id => new
                        {
                            id,
                            label = PartLabel(id),
                            fileCount = stats.TryGetValue(id, out var st) ? st.Count : 0,
                            bytes = stats.TryGetValue(id, out var st2) ? st2.Bytes : 0L,
                        }).ToArray(),
                        boxarts = boxArts.Select(b => new { entry = b.EntryName, path = b.FullPath, root = b.Root }).ToArray(),
                    };
                    ZipArchiveEntry m = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                    using Stream ms = m.Open();
                    JsonSerializer.Serialize(ms, manifest, new JsonSerializerOptions { WriteIndented = true });
                }

                if (failed > 0) AppLog.Warn("Backup", $"読めなかったファイルが {failed} 件ありました(その分は含まれていません)");
                File.Move(tempZip, zipPath, overwrite: true);
            }).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(tempZip); } catch { /* 何もしない */ }
            throw;
        }
        finally
        {
            if (dbCopy is not null) { try { File.Delete(dbCopy); } catch { /* 何もしない */ } }
        }

        AppLog.Info("Backup", $"バックアップを作りました: {zipPath} ({FormatSize(new FileInfo(zipPath).Length)}) 項目: {string.Join(",", selected)}");
        return zipPath;
    }

    private static string GetAppVersion()
    {
        string? info = typeof(BackupService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    private static void AddFile(ZipArchive zip, string fullPath, string entryName,
        Dictionary<string, (int Count, long Bytes)> stats, ref int failed)
    {
        try
        {
            // 使用中のファイルも読めるよう、共有を許可して開く。
            using var src = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // 画像・ステートは圧縮してもほぼ小さくならないので、速さを優先する。
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
            using Stream dst = entry.Open();
            src.CopyTo(dst);

            // 項目(entryNameの最初の区切りまで)ごとの、ファイル数と容量を数える。
            int slash = entryName.IndexOf('/');
            string part = slash > 0 ? entryName[..slash] : entryName;
            stats.TryGetValue(part, out var cur);
            stats[part] = (cur.Count + 1, cur.Bytes + src.Length);
        }
        catch (Exception ex)
        {
            failed++;
            AppLog.Warn("Backup", $"バックアップに含められませんでした: {fullPath}", ex);
        }
    }

    // ---- 一覧 ----

    /// <summary>保存先にあるバックアップを、新しい順に返す(RetroNexusのバックアップではないzipは除く)。</summary>
    public static async Task<IReadOnlyList<BackupInfo>> ListAsync()
    {
        return await Task.Run(() =>
        {
            string folder = GetFolder();
            var list = new List<BackupInfo>();
            if (!Directory.Exists(folder)) return (IReadOnlyList<BackupInfo>)list;

            string[] files;
            try { files = Directory.GetFiles(folder, "*.zip"); } catch { return list; }
            foreach (string f in files)
            {
                BackupInfo? info = ReadInfo(f);
                if (info is not null) list.Add(info);
            }
            return list.OrderByDescending(b => b.Created).ToList();
        }).ConfigureAwait(false);
    }

    private static BackupInfo? ReadInfo(string zipPath)
    {
        try
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            ZipArchiveEntry? m = zip.GetEntry(ManifestName);
            if (m is null) return null;
            using Stream s = m.Open();
            using JsonDocument doc = JsonDocument.Parse(s);
            JsonElement root = doc.RootElement;
            // 書式のバージョン。これより新しい書式のバックアップは、読み方が分からないので扱わない。
            int formatVersion = root.TryGetProperty("formatVersion", out JsonElement fv) && fv.TryGetInt32(out int fvi) ? fvi : 1;
            if (formatVersion > FormatVersion) return null;

            DateTime created = root.TryGetProperty("createdAt", out JsonElement c) && DateTime.TryParse(c.GetString(), out DateTime dt)
                ? dt : File.GetLastWriteTime(zipPath);
            var parts = new List<string>();
            if (root.TryGetProperty("parts", out JsonElement p) && p.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in p.EnumerateArray())
                    if (e.GetString() is string id) parts.Add(id);
            string ver = root.TryGetProperty("appVersion", out JsonElement v) ? v.GetString() ?? "" : "";
            return new BackupInfo(zipPath, created, new FileInfo(zipPath).Length, parts, ver);
        }
        catch
        {
            return null;
        }
    }

    // ---- 復元(予約→次回起動時に適用) ----

    /// <summary>復元を予約する。先に、今の状態を「復元前」のバックアップとして保存する。次回の起動時に適用される。</summary>
    public static async Task<string> StageRestoreAsync(BackupInfo backup, IEnumerable<string> parts)
    {
        var selected = new HashSet<string>(parts, StringComparer.Ordinal) { BasePartId };
        selected.IntersectWith(backup.Parts);

        // 復元で上書きされる項目を、先に保存する(戻したくなったときのため)。
        string safety = await CreateAsync(selected, "RetroNexus_復元前").ConfigureAwait(false);

        string json = JsonSerializer.Serialize(new { zip = backup.Path, parts = selected.ToArray() });
        File.WriteAllText(PendingPath, json);
        AppLog.Info("Backup", $"復元を予約しました: {backup.Path} 項目: {string.Join(",", selected)}");
        return safety;
    }

    /// <summary>起動の最初に呼ぶ。復元の予約があれば、zipの内容でファイルを上書きする。失敗しても、アプリは止めない。</summary>
    public static void ApplyPendingRestore()
    {
        if (!File.Exists(PendingPath)) return;

        try
        {
            string zipPath;
            HashSet<string> parts;
            using (JsonDocument pending = JsonDocument.Parse(File.ReadAllText(PendingPath)))
            {
                zipPath = pending.RootElement.GetProperty("zip").GetString() ?? string.Empty;
                parts = new HashSet<string>(
                    pending.RootElement.GetProperty("parts").EnumerateArray().Select(e => e.GetString() ?? string.Empty),
                    StringComparer.Ordinal);
            }

            if (!File.Exists(zipPath))
            {
                _restoreResult = "バックアップのファイルが見つかりませんでした。復元は行っていません。\n" + zipPath;
                AppLog.Warn("Backup", "復元を中止: zipが見つかりません: " + zipPath);
                return;
            }

            int restored = 0, failed = 0, skipped = 0;
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            // 表紙の画像: 元の場所(manifestの path)へ戻す。そのROMフォルダが今あるときだけ。
            var boxArts = new Dictionary<string, (string Path, string Root)>(StringComparer.Ordinal);
            ZipArchiveEntry? mEntry = zip.GetEntry(ManifestName);
            if (mEntry is not null)
            {
                using Stream ms = mEntry.Open();
                using JsonDocument md = JsonDocument.Parse(ms);
                if (md.RootElement.TryGetProperty("formatVersion", out JsonElement fv) && fv.TryGetInt32(out int fvi) && fvi > FormatVersion)
                {
                    _restoreResult = "このバックアップは、新しい版のRetroNexusで作られたため、復元できませんでした。アプリを更新してください。";
                    AppLog.Warn("Backup", $"復元を中止: 書式のバージョン {fvi} は未対応です");
                    return;
                }
                if (md.RootElement.TryGetProperty("boxarts", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement b in arr.EnumerateArray())
                        boxArts[b.GetProperty("entry").GetString() ?? string.Empty] =
                            (b.GetProperty("path").GetString() ?? string.Empty, b.GetProperty("root").GetString() ?? string.Empty);
            }

            // DBの古い付随ファイルが残っていると、復元したDBと食い違うので先に消す。
            if (parts.Contains(BasePartId))
            {
                foreach (string suffix in new[] { "-wal", "-shm" })
                {
                    try { File.Delete(Path.Combine(DataDir, DbFileName + suffix)); } catch { /* 無ければよい */ }
                }
            }

            string appFull = Path.GetFullPath(AppDir);
            foreach (ZipArchiveEntry entry in zip.Entries.ToList())
            {
                if (entry.FullName == ManifestName || entry.FullName.EndsWith('/')) continue;
                int slash = entry.FullName.IndexOf('/');
                if (slash <= 0) continue;
                string part = entry.FullName[..slash];
                if (!parts.Contains(part)) continue;

                string? target = null;
                if (boxArts.TryGetValue(entry.FullName, out var box))
                {
                    if (Directory.Exists(box.Root)) target = box.Path;
                    else { skipped++; continue; }   // そのROMフォルダが今は無い(外付けが未接続など)
                }
                else
                {
                    string rel = entry.FullName[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
                    string full = Path.GetFullPath(Path.Combine(AppDir, rel));
                    // 壊れた/細工されたzipが、アプリのフォルダの外へ書かないようにする。
                    if (!full.StartsWith(appFull, StringComparison.OrdinalIgnoreCase)) { failed++; continue; }
                    target = full;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string temp = target + ".restoring";
                    using (Stream src = entry.Open())
                    using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                        src.CopyTo(dst);
                    File.Move(temp, target, overwrite: true);
                    restored++;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Warn("Backup", $"復元できませんでした: {entry.FullName}", ex);
                }
            }

            _restoreResult = $"バックアップから復元しました({restored} ファイル)。"
                + (skipped > 0 ? $"\nROMフォルダが見つからない表紙の画像 {skipped} 件は、戻していません。" : "")
                + (failed > 0 ? $"\n復元できなかったファイルが {failed} 件あります(動作ログを確認してください)。" : "");
            AppLog.Info("Backup", $"復元しました: {zipPath} 復元 {restored} / 失敗 {failed} / 省略 {skipped}");
        }
        catch (Exception ex)
        {
            _restoreResult = "復元に失敗しました。\n" + ex.Message;
            AppLog.Error("Backup", "復元の適用に失敗しました", ex);
        }
        finally
        {
            try { File.Delete(PendingPath); } catch { /* 何もしない */ }
        }
    }

    /// <summary>起動時の復元の結果(あれば、1回だけ返す)。設定画面を開いたときに表示する。</summary>
    public static string? TakeRestoreResult()
    {
        string? r = _restoreResult;
        _restoreResult = null;
        return r;
    }
}
