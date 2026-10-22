﻿using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using EmulatorLauncher.Storage;
using EmulatorLauncher.Models;
using EmulatorLauncher.Net;
using EmulatorLauncher.Services;

namespace EmulatorLauncher.RomLibrary;

/// <summary>フォルダ新規登録リクエストの結果種別。</summary>
public enum RomFolderRegistrationStatus
{
    /// <summary>新規に登録し、スキャンも完了した。</summary>
    Registered,

    /// <summary>同じフォルダ(VolumeSerial+FolderIdが一致)が既に登録済みのため、拒否した。</summary>
    AlreadyRegistered,

    /// <summary>指定されたパスのフォルダが存在しない。</summary>
    FolderNotFound,

    /// <summary>NTFS識別情報の取得やDB操作等、その他の理由で失敗した。</summary>
    Failed,
}

/// <summary>フォルダ新規登録リクエストの結果。</summary>
public sealed class RomFolderRegistrationResult
{
    public required RomFolderRegistrationStatus Status { get; init; }

    /// <summary>登録済み(今回登録した、または既に登録されていた)RomFolder。失敗時はnull。</summary>
    public RomFolder? Folder { get; init; }

    /// <summary>今回のスキャンで登録できたRomFileの件数(新規登録時のみ意味を持つ)。</summary>
    public int ScannedFileCount { get; init; }

    /// <summary>AlreadyRegistered/FolderNotFound/Failed時の説明。</summary>
    public string? Message { get; init; }
}

/// <summary>フォルダ削除リクエストの結果種別。</summary>
public enum RomFolderRemovalStatus
{
    /// <summary>フォルダ、配下の全RomFile、コレクションへの所属を削除した。</summary>
    Removed,

    /// <summary>指定されたNoのRomFolderが見つからない。</summary>
    FolderNotFound,

    /// <summary>DB操作等、その他の理由で失敗した。</summary>
    Failed,
}

/// <summary>フォルダ削除リクエストの結果。</summary>
public sealed class RomFolderRemovalResult
{
    public required RomFolderRemovalStatus Status { get; init; }

    /// <summary>データベースから削除したRomFileの件数(成功時のみ意味を持つ)。</summary>
    public int RemovedFileCount { get; init; }

    /// <summary>FolderNotFound/Failed時の説明。</summary>
    public string? Message { get; init; }
}

/// <summary>登録済みフォルダの更新(新規ROM検出)リクエストの結果種別。</summary>
public enum RomFolderRefreshStatus
{
    /// <summary>更新を完了した(新規ROMが0件のこともある)。</summary>
    Refreshed,

    /// <summary>指定されたNoのRomFolderが見つからない。</summary>
    FolderNotFound,

    /// <summary>フォルダ自体がディスク上に存在しない(取り外し・移動・削除等)。</summary>
    PathMissing,

    /// <summary>スキャン中のエラー等、その他の理由で失敗した。</summary>
    Failed,
}

/// <summary>登録済みフォルダの更新(新規ROM検出)リクエストの結果。</summary>
public sealed class RomFolderRefreshResult
{
    public required RomFolderRefreshStatus Status { get; init; }

    /// <summary>今回新しく登録できたRomFileの件数。</summary>
    public int AddedFileCount { get; init; }

    /// <summary>更新前から登録されていたRomFileの件数。</summary>
    public int ExistingFileCount { get; init; }

    /// <summary>ディスク上から消えていたため、登録を削除したRomFileの件数。</summary>
    public int RemovedFileCount { get; init; }

    /// <summary>同じフォルダ内での名前変更として検出し、既存の登録を引き継いだ件数。</summary>
    public int RenamedFileCount { get; init; }

    /// <summary>FolderNotFound/PathMissing/Failed時の説明。</summary>
    public string? Message { get; init; }
}

/// <summary>
/// ROMフォルダの新規登録を行うサービス。
///
/// 「新規登録」の流れ:
///   1. パスが実在するフォルダかを確認する。
///   2. NTFSの識別情報(VolumeSerial+FolderId)を取得する。
///   3. 同じ識別情報のRomFolderが既に登録されていないか確認する(あれば拒否)。
///   4. RomFolderを登録する(この時点のLastModifyTimeAt/LastScannedAtは仮の値)。
///   5. フォルダ直下のファイルを列挙し、rdblist.json(RdbCatalogService)で定義された
///      拡張子、または中身に対応拡張子のROMを含むzipだけをRomFileとして登録する。
///      RetroArch RDB(libretro-database)をダウンロード済みなら照合し、一致すれば
///      Name/Description/Genre/ReleaseYear/Developer/Usersを反映する
///      (一致しなければ、Titleは拡張子を除いたファイル名のまま。ボックスアートだけは、
///      その「拡張子を除いたファイル名」をRDBの"name"相当とみなして検索を試みる。
///      Description/Genre等、名前以外のRDBフィールドは補える情報が無いため既定値のまま)。
///      照合方法はコンソールによって異なる:
///        - カートリッジ系: ROMのCRC-32
///        - ディスク系(<see cref="DiscSerialReader.UsesSerial"/>がtrueのpsx等): まずディスク
///          イメージからシリアル(PS1ならSYSTEM.CNFの"SLUS_009.08"→"SLUS-00908")を読み取り
///          RDBのserialで照合する。シリアルが読み取れなかった場合(SYSTEM.CNFが無い初期
///          タイトル等)のみ、データトラックのCRC-32で照合する。シリアルが読み取れたが
///          RDBに無かった場合(=RDB未収録)は、CRC-32へのフォールバックは行わない
///          (ディスクのCRC-32はダンプ方法で値が変わり照合に使えないうえ、CDサイズの
///          ファイル全体を読む重い処理のため)。
///      また、.cueから参照されている.bin等は、cue側で1本のゲームとして登録するため
///      個別には登録しない(マルチトラックのゲームが分裂して登録されないようにする)。
///   6. スキャンが完了したら、RomFolderのLastModifyTimeAt(フォルダの実際の最終更新日時)と
///      LastScannedAt(スキャンが完了した日時)を更新する。
///
/// 登録済みフォルダの更新(<see cref="RefreshFolderAsync"/>)は「新規ROMの検出」だけを行う
/// (まだ登録されていないファイル名だけを追加する。ディスクから消えたROMの登録解除や、
/// 既存RomFileの情報の更新は行わない)。
/// </summary>
public static class RomFolderScanService
{
    /// <summary>
    /// フォルダの新規登録リクエストを処理する。ゲーム実行中の裏で呼ばれても構わないよう、
    /// 完全に非同期(Task)で実装している。
    /// </summary>
    /// <param name="path">登録するフォルダの絶対パス(例: "E:\Roms\N64")。</param>
    /// <param name="consoleId">このフォルダに対応するコンソールのId(例: "n64")。</param>
    public static async Task<RomFolderRegistrationResult> RegisterFolderAsync(string path, string consoleId)
    {
        if (!Directory.Exists(path))
        {
            return new RomFolderRegistrationResult
            {
                Status = RomFolderRegistrationStatus.FolderNotFound,
                Message = $"フォルダが見つかりません: {path}",
            };
        }

        if (!NativeFileId.TryGetFileIdentity(path, out ulong volumeSerial, out Guid folderId))
        {
            return new RomFolderRegistrationResult
            {
                Status = RomFolderRegistrationStatus.Failed,
                Message = $"NTFSのフォルダ識別情報を取得できませんでした(FAT32等、対応していないファイルシステムの可能性があります): {path}",
            };
        }

        // 既に同じフォルダ(NTFS識別情報が一致)が登録されていないか確認する。
        // 論理削除済み(IsDeleted=true)のものも「既に存在する」として拒否対象に含める
        // (削除済みフォルダの復活は今回のスコープ外。RomFolderRepository.RestoreAsyncを
        // 使う専用の操作を後で用意する想定)。
        RomFolder? existing = await RomFolderRepository.FindByVolumeAndFolderIdAsync(volumeSerial, folderId)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new RomFolderRegistrationResult
            {
                Status = RomFolderRegistrationStatus.AlreadyRegistered,
                Folder = existing,
                Message = $"既に登録されています(No={existing.No}): {existing.Path}",
            };
        }

        var folder = new RomFolder
        {
            FolderId = folderId,
            VolumeSerial = volumeSerial,
            ConsoleId = consoleId,
            Path = path,
            // スキャン完了時に正しい値へ更新するため、ここでは仮の値を入れておく。
            LastModifyTimeAt = Directory.GetLastWriteTimeUtc(path),
            LastScannedAt = DateTime.MinValue,
            IsDeleted = false,
        };

        try
        {
            await RomFolderRepository.InsertAsync(folder).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RomFolderRegistrationResult
            {
                Status = RomFolderRegistrationStatus.Failed,
                Message = $"RomFolderの登録に失敗しました: {ex.Message}",
            };
        }

        int scannedCount;
        try
        {
            scannedCount = await ScanFolderContentsAsync(folder, path, consoleId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // フォルダ自体の登録は既に成功しているため、ここで失敗してもRomFolderの行は残す
            // (LastScannedAtが更新されないままになるので、「未スキャン」の状態として見分けられる)。
            return new RomFolderRegistrationResult
            {
                Status = RomFolderRegistrationStatus.Failed,
                Folder = folder,
                Message = $"フォルダ内のスキャン中にエラーが発生しました: {ex.Message}",
            };
        }

        // スキャンが完了したので、フォルダの最終更新日時とスキャン完了日時を確定させる。
        folder.LastModifyTimeAt = Directory.GetLastWriteTimeUtc(path);
        folder.LastScannedAt = DateTime.UtcNow;
        await RomFolderRepository.UpdateAsync(folder).ConfigureAwait(false);

        return new RomFolderRegistrationResult
        {
            Status = RomFolderRegistrationStatus.Registered,
            Folder = folder,
            ScannedFileCount = scannedCount,
        };
    }

    /// <summary>
    /// 登録済みフォルダ(RomFolder.No)を再スキャンする。
    ///   1. 登録済み(論理削除されていないもの)のうちディスク上に無いファイルへ、削除フラグ(IsDeleted)を立てる。
    ///   2. まだ登録されていないROMを新規として追加する(既に登録済みのファイル名は処理しない)。
    ///   3. 新規として見つかったファイルが、削除フラグ付きの既存エントリと同じ実体
    ///      (NTFSのVolumeSerial+FileIdが一致=同じフォルダ内の名前変更)なら、既存エントリの
    ///      ファイル名を新しい名前に変えて削除フラグを外し、重複して作られた新規エントリは捨てる。
    ///   4. スキャン後も削除フラグが残っているエントリを、コレクション所属ごとデータベースから物理削除する。
    /// ユーザー操作で論理削除されていたエントリ(スキャン前からIsDeleted=1)は触らない。
    /// フォルダ自体が見つからない場合は、切断されているだけの可能性があるため何もせずPathMissingを返す
    /// (<paramref name="forceMissing"/>がtrueなら、フォルダの登録ごと削除する)。
    /// 実際のROMファイルや画像はディスク上から一切削除しない。
    /// </summary>
    public static async Task<RomFolderRefreshResult> RefreshFolderAsync(int folderNo, bool forceMissing = false)
    {
        RomFolder? folder = await RomFolderRepository.GetAsync(folderNo).ConfigureAwait(false);
        if (folder is null)
        {
            return new RomFolderRefreshResult
            {
                Status = RomFolderRefreshStatus.FolderNotFound,
                Message = $"フォルダが見つかりません(No={folderNo})",
            };
        }

        if (!Directory.Exists(folder.Path))
        {
            if (!forceMissing)
            {
                return new RomFolderRefreshResult
                {
                    Status = RomFolderRefreshStatus.PathMissing,
                    Message = $"フォルダがディスク上に見つかりません: {folder.Path}",
                };
            }

            RomFolderRemovalResult removal = await RemoveFolderAsync(folderNo).ConfigureAwait(false);
            return new RomFolderRefreshResult
            {
                Status = removal.Status == RomFolderRemovalStatus.Removed
                    ? RomFolderRefreshStatus.Refreshed : RomFolderRefreshStatus.Failed,
                RemovedFileCount = removal.RemovedFileCount,
                Message = removal.Message,
            };
        }

        var flagged = new List<RomFile>();
        try
        {
            IReadOnlyList<RomFile> existing = await RomFileRepository
                .GetByParentIdAsync(folderNo, includeDeleted: true).ConfigureAwait(false);
            var knownNames = new HashSet<string>(
                existing.Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
            var existingNos = new HashSet<int>(existing.Select(f => f.No));

            // 1. ディスク上に無い(論理削除されていない)エントリへ削除フラグを立てる。
            List<RomFile> active = existing.Where(f => !f.IsDeleted).ToList();
            foreach (RomFile f in active)
            {
                string full = Path.Combine(folder.Path, f.FileName.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) flagged.Add(f);
            }

            // フォルダが空(マウント失敗・権限等)なのに全部消えている場合は、全消しを避けて何もしない。
            if (flagged.Count > 0 && flagged.Count == active.Count
                && !Directory.EnumerateFileSystemEntries(folder.Path).Any())
            {
                flagged.Clear();
                return new RomFolderRefreshResult
                {
                    Status = RomFolderRefreshStatus.Failed,
                    ExistingFileCount = existing.Count,
                    Message = "フォルダが空だったため、登録の削除は行いませんでした(ドライブの接続状態を確認してください)。",
                };
            }

            foreach (RomFile f in flagged)
            {
                await RomFileRepository.SoftDeleteAsync(f.No).ConfigureAwait(false);
            }

            // 2. 新規ROMの追加。
            int added = await ScanFolderContentsAsync(folder, folder.Path, folder.ConsoleId, knownNames)
                .ConfigureAwait(false);

            // 3. 名前変更の検出(同じ実体=VolumeSerial+FileIdが一致する新規エントリを探す)。
            int renamed = 0;
            if (flagged.Count > 0 && added > 0)
            {
                IReadOnlyList<RomFile> after = await RomFileRepository
                    .GetByParentIdAsync(folderNo, includeDeleted: true).ConfigureAwait(false);
                List<RomFile> fresh = after.Where(f => !existingNos.Contains(f.No)).ToList();

                foreach (RomFile old in flagged.ToList())
                {
                    if (old.FileId == Guid.Empty) continue;
                    List<RomFile> hits = fresh
                        .Where(n => n.FileId == old.FileId && n.VolumeSerial == old.VolumeSerial).ToList();
                    // 候補が1つに決まらない場合は取り違えを避け、名前変更とは見なさない。
                    if (hits.Count != 1) continue;
                    RomFile n = hits[0];

                    string oldStem = Path.GetFileNameWithoutExtension(old.FileName);
                    if (string.Equals(old.Title, oldStem, StringComparison.Ordinal))
                    {
                        old.Title = n.Title; // タイトルが元ファイル名のままなら、新しい名前に追従する。
                    }
                    old.FileName = n.FileName;
                    old.IsDeleted = false;
                    await RomFileRepository.UpdateAsync(old).ConfigureAwait(false);
                    await RomFileRepository.DeleteAsync(n.No).ConfigureAwait(false);
                    fresh.Remove(n);
                    flagged.Remove(old);
                    renamed++;
                    added--;
                }
            }

            // 4. 削除フラグが残っているエントリを物理削除する(コレクション所属を先に外す)。
            int removed = flagged.Count;
            if (removed > 0)
            {
                await RomCollectionRepository.RemoveRomFilesFromAllCollectionsAsync(
                    flagged.Select(f => f.No).ToList()).ConfigureAwait(false);
                foreach (RomFile f in flagged)
                {
                    await RomFileRepository.DeleteAsync(f.No).ConfigureAwait(false);
                }
                flagged.Clear();
            }

            folder.LastModifyTimeAt = Directory.GetLastWriteTimeUtc(folder.Path);
            folder.LastScannedAt = DateTime.UtcNow;
            await RomFolderRepository.UpdateAsync(folder).ConfigureAwait(false);

            return new RomFolderRefreshResult
            {
                Status = RomFolderRefreshStatus.Refreshed,
                AddedFileCount = added,
                ExistingFileCount = existing.Count,
                RemovedFileCount = removed,
                RenamedFileCount = renamed,
            };
        }
        catch (Exception ex)
        {
            // 途中で失敗した場合、削除フラグを立てただけのエントリは元に戻す(隠れたまま残さない)。
            foreach (RomFile f in flagged)
            {
                try { await RomFileRepository.RestoreAsync(f.No).ConfigureAwait(false); } catch { }
            }
            return new RomFolderRefreshResult
            {
                Status = RomFolderRefreshStatus.Failed,
                Message = $"フォルダの更新に失敗しました: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// 指定フォルダ(RomFolder.No)を、配下の全RomFileと合わせてデータベースから物理削除する。
    ///
    /// 削除対象のRomFileがいずれかのコレクション(「お気に入り」含む)に登録されていれば、
    /// RomFile自体を削除する前に、まずそちらの所属(RomCollectionItem)を削除する
    /// (RomCollectionItem.RomFileNoはRomFile.NoへのFOREIGN KEYのため、順序を守らないと
    /// 制約違反になる)。同様にRomFile.ParentIdはRomFolder.NoへのFOREIGN KEYのため、
    /// RomFolder自体を削除するのは配下のRomFileを消し終えた後にする。
    ///
    /// 実際のROMファイルや、ダウンロード済みのボックスアート画像(RomFolder.Path配下の
    /// RetroNexus\Named_Boxartsフォルダ)はディスク上から一切削除しない
    /// (あくまでデータベース上の登録を解除するだけ)。
    /// </summary>
    public static async Task<RomFolderRemovalResult> RemoveFolderAsync(int folderNo)
    {
        RomFolder? folder = await RomFolderRepository.GetAsync(folderNo).ConfigureAwait(false);
        if (folder is null)
        {
            return new RomFolderRemovalResult
            {
                Status = RomFolderRemovalStatus.FolderNotFound,
                Message = $"フォルダが見つかりません(No={folderNo})",
            };
        }

        try
        {
            // 論理削除済みのRomFileも含めて漏れなく対象にする(includeDeleted: true)。
            IReadOnlyList<RomFile> files = await RomFileRepository
                .GetByParentIdAsync(folderNo, includeDeleted: true).ConfigureAwait(false);

            List<int> fileNos = files.Select(f => f.No).ToList();
            await RomCollectionRepository.RemoveRomFilesFromAllCollectionsAsync(fileNos).ConfigureAwait(false);

            foreach (RomFile file in files)
            {
                await RomFileRepository.DeleteAsync(file.No).ConfigureAwait(false);
            }

            await RomFolderRepository.DeleteAsync(folderNo).ConfigureAwait(false);

            return new RomFolderRemovalResult
            {
                Status = RomFolderRemovalStatus.Removed,
                RemovedFileCount = files.Count,
            };
        }
        catch (Exception ex)
        {
            return new RomFolderRemovalResult
            {
                Status = RomFolderRemovalStatus.Failed,
                Message = $"フォルダの削除に失敗しました: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// フォルダ直下のファイルを列挙し、対象拡張子(またはその拡張子のROMを含むzip)だけを
    /// RomFileとして登録する。サブフォルダは、rdblist.jsonの拡張子に"folder(.gdi,.cdi)"形式の
    /// 指定があるコンソールだけ、直下のサブフォルダ内の対象ファイルを登録する(それ以外は対象外)。
    /// 登録できた件数を返す。knownFileNamesに含まれるファイル名(登録済み)はスキップする。
    /// </summary>
    private static async Task<int> ScanFolderContentsAsync(
        RomFolder folder, string path, string consoleId, HashSet<string>? knownFileNames = null)
    {
        // このスキャンの動作ログ(path\RetroNexus\log\scan_日時.log)。終了時に集計を書いて閉じる。
        using ScanLog scanLog = ScanLog.Begin(path);
        ScanLog.Info($"スキャン開始: コンソール={consoleId} / フォルダ={path} / "
            + (knownFileNames is null ? "新規登録" : $"再スキャン(登録済み {knownFileNames.Count}件は除外)"));

        // PS3は、ゲームごとのフォルダ(PS3_GAME\PARAM.SFO)から情報と画像を読むので、RDB・拡張子の仕組みを使わない。
        if (string.Equals(consoleId, "ps3", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanPs3FolderAsync(folder, path, knownFileNames).ConfigureAwait(false);
        }

        RdbSourceInfo? rdbSource = RdbCatalogService.FindForConsole(consoleId);
        var allowedExtensions = new HashSet<string>(
            rdbSource?.Extensions ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        if (allowedExtensions.Count == 0)
        {
            // rdblist.jsonに拡張子定義が無いコンソール(設定画面の「ランチャー管理」で新規に
            // 作ったコンソール等)は、Consoles.jsonの"romExtensions"を代わりに使う。
            EmulatorLauncher.Models.ConsoleInfo? consoleInfo = EmulatorLauncher.Models.SampleData.GetConsoles()
                .FirstOrDefault(c => string.Equals(c.Id, consoleId, StringComparison.OrdinalIgnoreCase));
            foreach (string extension in consoleInfo?.RomExtensions ?? new List<string>())
            {
                allowedExtensions.Add(extension);
            }
        }

        // "folder(.gdi,.cdi)" 形式の指定は、「直下のサブフォルダの中に、その拡張子のファイルが
        // 直接あれば、そのファイルを1ゲームとして登録する」という意味(ドリームキャスト等、
        // ゲームごとにフォルダへ入っている形式向け)。通常の拡張子の一覧からは取り除いておく。
        var folderExtensions = new List<string>();
        foreach (string entry in allowedExtensions.Where(e => e.StartsWith("folder(", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            allowedExtensions.Remove(entry);
            string inner = entry.Substring("folder(".Length).TrimEnd(')');
            foreach (string part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                folderExtensions.Add(part.StartsWith('.') ? part : "." + part);
            }
        }

        if (allowedExtensions.Count == 0 && folderExtensions.Count == 0)
        {
            // rdblist.jsonにこのコンソールの拡張子定義が無いと、何をROMとみなせばよいか
            // 判断できないため、スキャン自体を行わない(0件のまま)。
            System.Diagnostics.Debug.WriteLine(
                $"[RomFolderScanService] consoleId=\"{consoleId}\" の拡張子定義(rdblist.json)が見つからないため、スキャンしません: {path}");
            ScanLog.Warn($"拡張子の定義(rdblist.json / Consoles.json)が見つからないため、スキャンしません: {consoleId}");
            return 0;
        }

        // RDB(libretro-database)は未取得なら取得を試み、取得できればCRC-32/シリアルをキーにした
        // 辞書へ読み込んでおく(1フォルダのスキャン中に何度も読み直さないよう、ここで1回だけ)。
        // 取得・読み込みに失敗しても、その場合は単に「RDBと一致なし」として扱われるだけで、
        // スキャン自体は継続する(rdbが空のままになる)。
        ScanLog.Info($"対象拡張子: {string.Join(" ", allowedExtensions.OrderBy(e => e))}"
            + (folderExtensions.Count > 0 ? $" / フォルダ単位: {string.Join(" ", folderExtensions)}" : "")
            + $" / 照合方式: {(string.Equals(rdbSource?.MatchBy, "romName", StringComparison.OrdinalIgnoreCase) ? "ROMセット名" : "CRC-32/シリアル")}");

        var rdb = new RdbDatabase();
        string? rdbPath = await RdbCatalogService.EnsureRdbDownloadedAsync(consoleId).ConfigureAwait(false);
        if (rdbPath is not null)
        {
            rdb = await RetroArchRdbReader.LoadDatabaseAsync(rdbPath).ConfigureAwait(false);
            ScanLog.Info($"RDB: {Path.GetFileName(rdbPath)}(CRC {rdb.ByCrc.Count}件 / シリアル {rdb.BySerial.Count}件 / ROM名 {rdb.ByRomBaseName.Count}件)");
            if (rdb.NamelessCount > 0)
            {
                ScanLog.Info($"RDBに名前の無いエントリが {rdb.NamelessCount}件あり、うち {rdb.BorrowedNameCount}件は同じシリアルの名前付きエントリから名前を補いました。");
            }
        }
        else
        {
            // RDBが無いコンソールは、rdblist.jsonに"dat"があればDATファイルから同じ形で読み込む。
            string? datPath = await RdbCatalogService.EnsureDatDownloadedAsync(consoleId).ConfigureAwait(false);
            if (datPath is not null)
            {
                rdb = await DatReader.LoadDatabaseAsync(datPath).ConfigureAwait(false);
                ScanLog.Info($"DAT: {Path.GetFileName(datPath)}(CRC {rdb.ByCrc.Count}件 / シリアル {rdb.BySerial.Count}件 / ROM名 {rdb.ByRomBaseName.Count}件)");
            }
            else
            {
                ScanLog.Warn("RDB/DATを取得できませんでした(ネットワーク不通・定義なし等)。全ゲームが「未認識」になります。");
            }
        }

        if (string.IsNullOrEmpty(rdbSource?.Image))
        {
            ScanLog.Info("ボックスアートのURL(rdblist.jsonのimage)が未設定のため、画像は取得しません。");
        }

        // "bulk"指定のコンソールは、画像を最初にまとめて(並列で)取得しておく。
        // 各ゲームの画像はそのキャッシュからコピーされる(ResolveAndDownloadBoxArtAsync参照)。
        if (rdbSource?.Bulk == true)
        {
            await RdbCatalogService.EnsureBulkImagesAsync(rdbSource).ConfigureAwait(false);
        }

        // .cueが対象拡張子に含まれるコンソールでは、cueから参照されている.bin等を
        // 個別のゲームとして登録しないよう、先に集めておく。
        HashSet<string> cueReferencedFiles = allowedExtensions.Contains(".cue")
            ? CollectCueReferencedFiles(path)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int scannedCount = 0;
        int scannedFileCount = 0;

        foreach (string filePath in Directory.EnumerateFiles(path))
        {
            if (cueReferencedFiles.Contains(Path.GetFullPath(filePath)))
            {
                continue;
            }

            if (knownFileNames is not null && knownFileNames.Contains(Path.GetFileName(filePath)))
            {
                continue;
            }

            string extension = Path.GetExtension(filePath);

            // GD-ROM系(NAOMI等)は「ゲーム名.zip(キー用の小さなスタブ)」と「ゲーム名/(中にCHD)」が
            // 並ぶ構成。フォルダ単位で登録するので、同名フォルダのあるアーカイブは登録しない。
            if (folderExtensions.Count > 0
                && (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase))
                && Directory.Exists(Path.Combine(path, Path.GetFileNameWithoutExtension(filePath)))
                && FindFolderGameFile(Path.Combine(path, Path.GetFileNameWithoutExtension(filePath)), folderExtensions) is not null)
            {
                ScanLog.Skipped($"{Path.GetFileName(filePath)} → 同名フォルダ内のファイルを登録するため、登録しませんでした");
                continue;
            }

            scannedFileCount++;

            RomFile? file;
            if (string.Equals(rdbSource?.MatchBy, "romName", StringComparison.OrdinalIgnoreCase)
                && allowedExtensions.Contains(extension))
            {
                // ファイル名ベース照合のコンソール(アーケード系等)。zip/7z等の中身は展開・
                // 解析せず、ファイル自体を1ゲームとして扱う(下記BuildRomFileByNameAsync参照)。
                file = await BuildRomFileByNameAsync(filePath, folder.No, consoleId, rdbSource, path, rdb)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                file = await BuildRomFileFromZipAsync(filePath, allowedExtensions, folder.No, consoleId, rdbSource, path, rdb)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase)
                && allowedExtensions.Contains(".7z"))
            {
                // 7zは、rdblist.jsonの拡張子に".7z"を明示した機種でだけ中身を見る。
                file = await BuildRomFileFromSevenZipAsync(filePath, allowedExtensions, folder.No, consoleId, rdbSource, path, rdb)
                    .ConfigureAwait(false);
            }
            else if (allowedExtensions.Contains(extension))
            {
                file = await BuildRomFileFromPlainFileAsync(filePath, folder.No, consoleId, rdbSource, path, rdb)
                    .ConfigureAwait(false);
            }
            else
            {
                file = null;
            }

            if (file is null)
            {
                // 対象拡張子でもzipでもないファイル、またはzipの中身が対象拡張子と
                // 一致しなかった場合はここに来る。登録せずスキップする。
                ScanLog.Skipped($"{Path.GetFileName(filePath)} → 登録しませんでした"
                    + "(対象外の拡張子 / アーカイブ内に対象拡張子のファイルが無い / 読み取り失敗。詳しくは直前の警告を参照)");
                continue;
            }

            await RomFileRepository.InsertAsync(file).ConfigureAwait(false);
            ScanLog.Registered();
            scannedCount++;
        }

        // --- 直下のサブフォルダ(folder(...)指定) ---
        // 各サブフォルダの直下に対象拡張子のファイルがあれば、そのファイルを登録する。
        // FileNameには「サブフォルダ名/ファイル名」の相対パスを入れる(親フォルダのPathと結合して使う)。
        if (folderExtensions.Count > 0)
        {
            foreach (string directory in SafeEnumerateDirectories(path))
            {
                string directoryName = Path.GetFileName(directory);
                if (directoryName.StartsWith('$') || directoryName.StartsWith('.')
                    || string.Equals(directoryName, "RetroNexus", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // ごみ箱・隠しフォルダ・ボックスアート保存先
                }

                string? found = FindFolderGameFile(directory, folderExtensions);
                if (found is null)
                {
                    ScanLog.Skipped($"{directoryName}/ → フォルダ内に {string.Join(" ", folderExtensions)} のファイルが無い");
                    continue;
                }

                string relativeName = directoryName + "/" + Path.GetFileName(found);

                // NAOMI等のGD-ROM: 起動にはCHDではなく、同名の小さなzip(スタブ)を指定する必要がある
                // (CHDを直接渡すとFlycastがドリームキャストとして起動してしまう)。
                // 認識はCHDで行い、登録するファイルはスタブのzip/7zにする。
                string? launchStub = null;
                if (string.Equals(rdbSource?.MatchBy, "romName", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetExtension(found), ".chd", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string stubExt in new[] { ".zip", ".7z" })
                    {
                        if (File.Exists(Path.Combine(path, directoryName + stubExt)))
                        {
                            launchStub = directoryName + stubExt;
                            break;
                        }
                    }
                }

                if (launchStub is not null)
                {
                    relativeName = launchStub;
                }

                if (knownFileNames is not null && knownFileNames.Contains(relativeName))
                {
                    continue;
                }

                RomFile? file;
                if (string.Equals(rdbSource?.MatchBy, "romName", StringComparison.OrdinalIgnoreCase))
                {
                    file = await BuildRomFileByNameAsync(found, folder.No, consoleId, rdbSource, path, rdb)
                        .ConfigureAwait(false);
                }
                else
                {
                    file = await BuildRomFileFromPlainFileAsync(found, folder.No, consoleId, rdbSource, path, rdb)
                        .ConfigureAwait(false);
                }

                if (file is null)
                {
                    ScanLog.Skipped($"{relativeName} → 登録しませんでした(ID取得失敗/読み取り失敗)");
                    continue;
                }

                file.FileName = relativeName;
                await RomFileRepository.InsertAsync(file).ConfigureAwait(false);
                ScanLog.Registered();
                scannedCount++;
            }
        }

        ScanLog.Info($"直下のファイル {scannedFileCount}件を確認しました。");

        // 複数枚組(.m3u)のメンバー対応表を、フォルダ内の.m3uの中身に合わせる。
        if (allowedExtensions.Contains(".m3u"))
        {
            await RomDiscSetService.SyncFolderAsync(folder.No, path).ConfigureAwait(false);
        }

        return scannedCount;
    }

    /// <summary>
    /// PS3(RPCS3)用のスキャン。直下の各サブフォルダについて、PS3_GAME\PARAM.SFO(または直下のPARAM.SFO)と
    /// USRDIR\EBOOT.BINがあれば1ゲームとして登録する。タイトルとタイトルIDはPARAM.SFOから、
    /// 画像はICON0.PNGを「{親フォルダ}\RetroNexus\Named_Boxarts\{タイトルID}.png」へコピーして使う
    /// (元のファイルは変更しない。コピー先に同名があれば上書きせずそのまま使う)。
    /// FileNameには「サブフォルダ名/PS3_GAME/USRDIR/EBOOT.BIN」の相対パスを入れる(起動時はEBOOT.BINを渡す)。
    /// </summary>
    private static async Task<int> ScanPs3FolderAsync(RomFolder folder, string path, HashSet<string>? knownFileNames)
    {
        int scannedCount = 0;

        foreach (string directory in SafeEnumerateDirectories(path))
        {
            string directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith('$') || directoryName.StartsWith('.')
                || string.Equals(directoryName, "RetroNexus", StringComparison.OrdinalIgnoreCase))
            {
                continue; // ごみ箱・隠しフォルダ・ボックスアート保存先
            }

            // PARAM.SFOのあるフォルダ(ゲームの実体)を探す。
            string gameDirectory;
            string relativeEboot;
            if (File.Exists(Path.Combine(directory, "PS3_GAME", "PARAM.SFO")))
            {
                gameDirectory = Path.Combine(directory, "PS3_GAME");
                relativeEboot = directoryName + "/PS3_GAME/USRDIR/EBOOT.BIN";
            }
            else if (File.Exists(Path.Combine(directory, "PARAM.SFO")))
            {
                gameDirectory = directory;
                relativeEboot = directoryName + "/USRDIR/EBOOT.BIN";
            }
            else
            {
                ScanLog.Skipped($"{directoryName}/ → PARAM.SFO が見つからない(PS3_GAME\\PARAM.SFO または直下)");
                continue;
            }

            string ebootPath = Path.Combine(gameDirectory, "USRDIR", "EBOOT.BIN");
            if (!File.Exists(ebootPath))
            {
                ScanLog.Skipped($"{directoryName}/ → USRDIR\\EBOOT.BIN が見つからない");
                continue;
            }

            if (knownFileNames is not null && knownFileNames.Contains(relativeEboot)) continue;

            if (!NativeFileId.TryGetFileIdentity(ebootPath, out ulong volumeSerial, out Guid fileId)) continue;

            RomFile file = CreateBaseRomFile(ebootPath, folder.No, fileId, volumeSerial, "ps3");
            file.FileName = relativeEboot;

            ParamSfo? sfo = ParamSfoReader.TryRead(Path.Combine(gameDirectory, "PARAM.SFO"));
            file.Title = !string.IsNullOrEmpty(sfo?.Title) ? sfo!.Title : directoryName;

            // 画像: ICON0.PNGをコピー(保存名はタイトルID。無ければフォルダ名)。
            string boxArtKey = SanitizeForFileName(!string.IsNullOrEmpty(sfo?.TitleId) ? sfo!.TitleId : directoryName);
            string localFileName = boxArtKey + ".png";
            string destinationPath = Path.Combine(path, "RetroNexus", "Named_Boxarts", localFileName);
            try
            {
                string iconPath = Path.Combine(gameDirectory, "ICON0.PNG");
                if (!File.Exists(destinationPath) && File.Exists(iconPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(iconPath, destinationPath, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] PS3の画像コピーに失敗しました({directoryName}): {ex.Message}");
                ScanLog.Warn($"PS3の画像コピーに失敗: {directoryName}: {ex.Message}");
            }

            if (File.Exists(destinationPath)) file.IconPath = localFileName;

            ScanLog.Recognized(relativeEboot, file.Title, boxArtKey);
            ScanLog.Art(File.Exists(destinationPath) ? ScanLog.ArtResult.Existing : ScanLog.ArtResult.None, file.Title,
                File.Exists(destinationPath) ? $"ICON0.PNG を使用 ({localFileName})" : "ICON0.PNG が無いため画像なし");

            await RomFileRepository.InsertAsync(file).ConfigureAwait(false);
            ScanLog.Registered();
            scannedCount++;
        }

        return scannedCount;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// サブフォルダの直下から、folderExtensionsの先頭の拡張子から順に探して最初に見つかったファイルを返す
    /// (例: .gdiと.cdiの両方があれば.gdi)。無ければnull。
    /// </summary>
    private static string? FindFolderGameFile(string directory, List<string> folderExtensions)
    {
        try
        {
            List<string> files = Directory.EnumerateFiles(directory).ToList();
            foreach (string extension in folderExtensions)
            {
                string? match = files
                    .Where(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (match is not null) return match;
            }
        }
        catch
        {
            // アクセスできないフォルダは対象外
        }

        return null;
    }

    /// <summary>フォルダ内の全.cueから参照されているファイル(絶対パス)を集める。</summary>
    private static HashSet<string> CollectCueReferencedFiles(string folderPath)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string cuePath in Directory.EnumerateFiles(folderPath, "*.cue"))
        {
            foreach (string file in DiscSerialReader.GetCueReferencedFiles(cuePath))
            {
                referenced.Add(file);
            }
        }

        return referenced;
    }

    /// <summary>
    /// 拡張子が直接一致するROMファイル(圧縮されていない.z64/.cue/.bin等)からRomFileを組み立てる。
    /// カートリッジ系はファイル内容全体のCRC-32で照合する(計算できなければ登録しない)。
    /// ディスク系はシリアルで照合し、シリアルが読み取れなかった場合のみデータトラックの
    /// CRC-32へフォールバックする(シリアルは読み取れたがRDBに無かった場合はCRC-32を
    /// 計算しない)。どの方法でも一致しなくても登録はする(ファイル名ベースのTitleのまま)。
    /// その場合もボックスアートの検索(<see cref="ApplyUnmatchedAsync"/>)だけは行う。
    /// </summary>
    private static async Task<RomFile?> BuildRomFileFromPlainFileAsync(
        string filePath, int parentFolderNo, string consoleId, RdbSourceInfo? rdbSource, string folderPath,
        RdbDatabase rdb)
    {
        if (string.Equals(Path.GetExtension(filePath), ".m3u", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildRomFileFromM3uAsync(filePath, parentFolderNo, consoleId, rdbSource, folderPath, rdb)
                .ConfigureAwait(false);
        }

        return await BuildRomFileFromPlainFileCoreAsync(
            filePath, parentFolderNo, consoleId, rdbSource, folderPath, rdb, contentPath: null).ConfigureAwait(false);
    }

    /// <summary>
    /// 複数枚組のゲーム(.m3u)のRomFileを組み立てる。登録するファイルは.m3u自身(FileId等は.m3uのもの)で、
    /// 認識(シリアル/CRC-32の照合と、メタデータ・ボックスアートの取得)は、.m3uに書かれた1枚目のディスクで行う。
    /// タイトルは、RDBの名前(「…(Disc 1)」になる)ではなく、.m3uのファイル名にする(編集画面で変えられる)。
    /// 1枚目のファイルが見つからなくても、.m3uとしては登録する(起動時に、足りないディスクを知らせる)。
    /// </summary>
    private static async Task<RomFile?> BuildRomFileFromM3uAsync(
        string m3uPath, int parentFolderNo, string consoleId, RdbSourceInfo? rdbSource, string folderPath,
        RdbDatabase rdb)
    {
        string? first = RomDiscSetService.ResolveMemberPaths(m3uPath).FirstOrDefault(File.Exists);
        if (first is null)
        {
            ScanLog.Warn($"{Path.GetFileName(m3uPath)}: 1枚目のディスクが見つかりません(.m3uの中身を確認してください)");
            if (!NativeFileId.TryGetFileIdentity(m3uPath, out ulong volumeSerial, out Guid fileId)) return null;
            return CreateBaseRomFile(m3uPath, parentFolderNo, fileId, volumeSerial, consoleId);
        }

        RomFile? file = await BuildRomFileFromPlainFileCoreAsync(
            m3uPath, parentFolderNo, consoleId, rdbSource, folderPath, rdb, contentPath: first).ConfigureAwait(false);
        if (file is null) return null;

        file.Title = Path.GetFileNameWithoutExtension(m3uPath);
        file.TitleFromRdb = false;
        return file;
    }

    /// <summary>
    /// <paramref name="filePath"/>は登録するファイル(FileId・ファイル名・既定のタイトルの元)、
    /// <paramref name="contentPath"/>は照合のために中身を読むファイル(省略時はfilePathと同じ。
    /// .m3uでは1枚目のディスクを渡す)。
    /// </summary>
    private static async Task<RomFile?> BuildRomFileFromPlainFileCoreAsync(
        string filePath, int parentFolderNo, string consoleId, RdbSourceInfo? rdbSource, string folderPath,
        RdbDatabase rdb, string? contentPath)
    {
        if (!NativeFileId.TryGetFileIdentity(filePath, out ulong volumeSerial, out Guid fileId))
        {
            // 識別情報が取れないファイルは登録せずスキップする(理論上は稀なケース)。
            return null;
        }

        RomFile file = CreateBaseRomFile(filePath, parentFolderNo, fileId, volumeSerial, consoleId);
        string readPath = contentPath ?? filePath;

        if (DiscSerialReader.UsesSerial(consoleId))
        {
            // 1. シリアルで照合する。
            string? serial = await DiscSerialReader.ReadSerialAsync(consoleId, readPath).ConfigureAwait(false);
            if (serial is not null && rdb.TryFindBySerial(serial, out RdbGameEntry? serialEntry))
            {
                await ApplyRdbEntryAsync(file, serialEntry, SanitizeForFileName(serial), rdbSource, folderPath)
                    .ConfigureAwait(false);
                ApplyJapaneseInfo(file, consoleId, null, serial, readPath);
                return file;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[RomFolderScanService] シリアルで一致しませんでした(serial={serial ?? "(読み取り不可)"}): {filePath}");
            if (serial is null) ScanLog.Info($"{Path.GetFileName(filePath)}: シリアルを読み取れませんでした(CRCで照合します)");

            if (serial is not null)
            {
                // シリアルは読み取れたが、RDBにそのシリアルのエントリが無かった
                // (=このゲームはRDB未収録)。CRC-32はディスクの作り方(bin/cue分割・iso変換等)で
                // 変わってしまい照合の意味が薄いうえ、CDサイズのファイル全体を読む重い処理なので、
                // フォールバックはせずここで諦める(ファイル名ベースのTitleのまま登録する)。
                // ボックスアートだけは、ファイル名をRDBの"name"相当とみなして検索を試みる。
                await ApplyUnmatchedAsync(file, rdbSource, folderPath, $"シリアル {serial} がRDBに無い", rdb).ConfigureAwait(false);
                ApplyJapaneseInfo(file, consoleId, null, serial, readPath);
                return file;
            }

            // 2. フォールバック: シリアル自体が読み取れなかった場合のみ、データトラック
            // (.cueなら参照先の.bin)のCRC-32で照合する(SYSTEM.CNFが無い初期タイトル等)。
            await ApplyDiscCrcFallbackAsync(file, readPath, consoleId, rdb, rdbSource, folderPath).ConfigureAwait(false);
            return file;
        }

        if (DiscSerialReader.IsDiscBased(consoleId))
        {
            // シリアルが存在しないディスク系(PCエンジンCD等)。.cue/.chdを考慮した
            // CRC-32照合だけを行う(シリアル抽出は最初から試みない)。
            await ApplyDiscCrcFallbackAsync(file, readPath, consoleId, rdb, rdbSource, folderPath).ConfigureAwait(false);
            return file;
        }

        uint? crc = await TryComputeFileCrcAsync(filePath).ConfigureAwait(false);
        if (crc is not uint cartridgeCrc)
        {
            return null;
        }

        uint matchedCrc = cartridgeCrc;
        if (!rdb.ByCrc.TryGetValue(cartridgeCrc, out RdbGameEntry? entry)
            && RomNormalizer.MayNeedNormalization(consoleId))
        {
            // 生のCRCで一致しなければ、ヘッダ除去・バイト順の正規化をしたCRCでもう一度照合する。
            uint? normalizedCrc = await Task.Run(() => TryComputeNormalizedFileCrc(consoleId, filePath)).ConfigureAwait(false);
            if (normalizedCrc is uint normalized)
            {
                matchedCrc = normalized;
                rdb.ByCrc.TryGetValue(normalized, out entry);
            }
        }

        if (entry is not null)
        {
            await ApplyRdbEntryAsync(file, entry, $"{matchedCrc:X8}", rdbSource, folderPath).ConfigureAwait(false);
        }
        else if (!await TryApplyGameCodeAsync(file, consoleId, filePath, null, rdb, rdbSource, folderPath).ConfigureAwait(false))
        {
            await ApplyUnmatchedAsync(file, rdbSource, folderPath,
                matchedCrc == cartridgeCrc
                    ? $"CRC-32 {cartridgeCrc:X8} がRDBに無い"
                    : $"CRC-32 {cartridgeCrc:X8}(生) / {matchedCrc:X8}(ヘッダ除去・並び補正後) がどちらもRDBに無い", rdb).ConfigureAwait(false);
        }

        ApplyJapaneseInfo(file, consoleId, matchedCrc, null, filePath);
        return file;
    }

    /// <summary>
    /// CRC-32がRDBに無かったGBA/NDSのROMを、ヘッダ内のゲームコード(RDBのシリアル列と同じ値)で照合する。
    /// 別ダンプ・パッチ済み・日本語化版を同じゲームとして認識するためのもの。entryNameがnullなら
    /// filePath自体、そうでなければfilePath(アーカイブ)内のエントリを読む。一致したらtrue。
    /// </summary>
    private static async Task<bool> TryApplyGameCodeAsync(
        RomFile file, string consoleId, string filePath, string? entryName,
        RdbDatabase rdb, RdbSourceInfo? rdbSource, string folderPath)
    {
        if (!CartridgeGameCode.Supports(consoleId)) return false;

        string? code = await Task.Run(() => entryName is null
            ? CartridgeGameCode.TryReadFromFile(filePath, consoleId)
            : CartridgeGameCode.TryReadFromArchive(filePath, entryName, consoleId)).ConfigureAwait(false);
        if (code is null || !rdb.TryFindByGameCode(code, out RdbGameEntry? entry)) return false;

        await ApplyRdbEntryAsync(file, entry, SanitizeForFileName(code), rdbSource, folderPath).ConfigureAwait(false);
        ScanLog.Info($"{file.FileName}: CRC-32は不一致でしたが、ゲームコード {code} で「{file.Title}」と認識しました(別版・パッチ済みの可能性)");
        return true;
    }

    /// <summary>
    /// 3DSのROM(ファイル名がRDBのROM名と違う、復号済みダンプ等)を、NCCHヘッダの製品コード
    /// ("CTR-P-XXXX"。RDBのシリアル列と同じ)で照合する。地域違いで同じコードのエントリが
    /// 複数あるときは、ファイル名の地域に合うものを選ぶ。一致したらtrue。
    /// </summary>
    private static async Task<bool> TryApplyNintendo3dsProductCodeAsync(
        RomFile file, string filePath, string baseName, RdbDatabase rdb, RdbSourceInfo? rdbSource, string folderPath)
    {
        string? code = await Task.Run(() => CartridgeGameCode.TryReadNintendo3dsProductCode(filePath)).ConfigureAwait(false);
        if (code is null || !rdb.TryFindBySerialForFile(code, baseName, out RdbGameEntry? entry)) return false;

        string key = SanitizeForFileName(Path.GetFileNameWithoutExtension(entry.RomName ?? entry.Name ?? code));
        await ApplyRdbEntryAsync(file, entry, key, rdbSource, folderPath).ConfigureAwait(false);
        ScanLog.Info($"{file.FileName}: ファイル名はRDBと一致しませんでしたが、製品コード {code} で「{file.Title}」と認識しました");
        return true;
    }

    /// <summary>
    /// Wii Uの.wua(ファイル名がDATのROM名と違うもの。例: "MARIO KART 8 (JP).wua")を、中のmeta.xmlの
    /// 製品コード末尾4文字(DATのシリアル列と同じ)で照合する。DATに無いゲームでも、読めたIDで
    /// 日本語表(data/jdb/wiiu.csv)のSerial列を引く。読めなければfalse。
    /// </summary>
    private static async Task<bool> TryApplyWiiUGameCodeAsync(
        RomFile file, string filePath, string baseName, RdbDatabase rdb, RdbSourceInfo? rdbSource, string folderPath)
    {
        string? readError = null;
        string? code = await Task.Run(() => WuaMetaReader.TryReadGameCode(filePath, out readError)).ConfigureAwait(false);
        if (code is null)
        {
            ScanLog.Info($"{file.FileName}: ゲームIDを読み取れませんでした({readError})");
            return false;
        }

        if (rdb.TryFindBySerialForFile(code, baseName, out RdbGameEntry? entry))
        {
            string key = SanitizeForFileName(Path.GetFileNameWithoutExtension(entry.RomName ?? entry.Name ?? code));
            await ApplyRdbEntryAsync(file, entry, key, rdbSource, folderPath).ConfigureAwait(false);
            ScanLog.Info($"{file.FileName}: ファイル名はDATと一致しませんでしたが、ゲームID {code} で「{file.Title}」と認識しました");
            ApplyJapaneseInfo(file, "wiiu", null, code, filePath, baseName);
            return true;
        }

        // DATの日本版が無い(欧米版だけ)ゲームや、DATのシリアルが6文字("ARKJ01")のものは、
        // 日本語表(data/jdb/wiiu.csv)のSerial列(4文字のゲームID)で引く。英語タイトルが分かれば画像探しにも使う。
        if (JapaneseGameDb.HasTable("wiiu") && JapaneseGameDb.TryFind("wiiu", null, code, out JapaneseGameInfo tableInfo))
        {
            if (tableInfo.EnglishTitle.Length > 0) file.Title = tableInfo.EnglishTitle;
            await ApplyUnmatchedAsync(file, rdbSource, folderPath, $"ゲームID {code} がDATに無い").ConfigureAwait(false);
            ApplyJapaneseInfo(file, "wiiu", null, code, filePath, baseName);
            ScanLog.Info($"{file.FileName}: ゲームID {code} を日本語表で「{tableInfo.Title}」と認識しました");
            return true;
        }

        ScanLog.Info($"{file.FileName}: ゲームID {code} がDATにも日本語表にもありません");
        return false;
    }

    /// <summary>
    /// ROMセット名でRDBに一致しなかったzip/7zを、中身のファイルのCRC-32で照合する。RDBの"rom_name"が
    /// ゲームのセット名ではなくチップ単位のファイル名(Atomiswaveの"ax3201m01.mrom1"等)になっている
    /// コンソール向け。zip/7zのエントリが持つCRC-32(展開不要)のうち、RDBのエントリと一致したものが
    /// 最も多いゲームを選ぶ。一致したらtrue。
    /// </summary>
    private static async Task<bool> TryApplyArchiveEntryCrcAsync(
        RomFile file, string filePath, string baseName, RdbDatabase rdb, RdbSourceInfo? rdbSource, string folderPath)
    {
        if (!ArchiveReader.IsSupported(filePath)) return false;

        IReadOnlyList<ArchiveEntryInfo> entries;
        try
        {
            entries = await Task.Run(() => ArchiveReader.ListEntries(filePath)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] アーカイブの一覧を取得できませんでした({filePath}): {ex.Message}");
            return false;
        }

        var counts = new Dictionary<RdbGameEntry, int>();
        foreach (ArchiveEntryInfo item in entries)
        {
            // rom_nameが"xxx.zip"のセット名のRDB(FBNeo等)は対象外にする。中身のファイルが他のセットと
            // 共通のことがあり、別のゲームに結び付いてしまうため。チップ単位のRDBだけを対象にする。
            if (item.Size > 0 && rdb.ByCrc.TryGetValue(item.Crc, out RdbGameEntry? hit) && !string.IsNullOrWhiteSpace(hit.Name)
                && !ArchiveReader.IsSupported(hit.RomName ?? string.Empty))
            {
                counts[hit] = counts.GetValueOrDefault(hit) + 1;
            }
        }

        if (counts.Count == 0) return false;

        RdbGameEntry entry = counts.OrderByDescending(kv => kv.Value).First().Key;
        await ApplyRdbEntryAsync(file, entry, SanitizeForFileName(baseName), rdbSource, folderPath).ConfigureAwait(false);
        ScanLog.Info($"{file.FileName}: ROMセット名はRDBと一致しませんでしたが、中身のファイルのCRC-32で「{file.Title}」と認識しました");
        return true;
    }

    /// <summary>プレーンファイルの正規化CRC-32(ヘッダ除去・バイト順の変換後)。該当しなければnull。</summary>
    private static uint? TryComputeNormalizedFileCrc(string consoleId, string filePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            return RomNormalizer.TryComputeCrc(stream, consoleId, stream.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] 正規化CRCの計算に失敗しました({filePath}): {ex.Message}");
            return null;
        }
    }

    /// <summary>アーカイブ内のエントリの正規化CRC-32。該当しなければnull。</summary>
    private static uint? TryComputeNormalizedEntryCrc(string consoleId, string archivePath, string entryName, long entrySize)
    {
        try
        {
            return ArchiveReader.WithEntryStream(archivePath, entryName,
                stream => RomNormalizer.TryComputeCrc(stream, consoleId, entrySize));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] アーカイブ内の正規化CRCの計算に失敗しました({archivePath}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// ディスク系コンソール向けのCRC-32照合(<see cref="DiscSerialReader.GetCrcTargetPath"/>で
    /// .cueのデータトラック・.chd等を考慮したうえで計算する)。一致すればRDBエントリを適用し、
    /// 一致しなければ<see cref="ApplyUnmatchedAsync"/>でボックスアート検索だけ試みる。
    /// シリアルが存在しないコンソール(PCエンジンCD等)からも、シリアル読み取り失敗時の
    /// フォールバックとしても、共通で使う。
    /// </summary>
    private static async Task ApplyDiscCrcFallbackAsync(
        RomFile file, string filePath, string consoleId, RdbDatabase rdb, RdbSourceInfo? rdbSource, string folderPath)
    {
        uint? discCrcValue = null;
        string? crcTarget = DiscSerialReader.GetCrcTargetPath(filePath);
        RdbGameEntry? crcEntry = null;
        if (crcTarget is not null)
        {
            uint? discCrc = await TryComputeFileCrcAsync(crcTarget).ConfigureAwait(false);
            if (discCrc is uint value)
            {
                discCrcValue = value;
                rdb.ByCrc.TryGetValue(value, out crcEntry);
                if (crcEntry is not null)
                {
                    await ApplyRdbEntryAsync(file, crcEntry, $"{value:X8}", rdbSource, folderPath).ConfigureAwait(false);
                }
            }
        }

        if (crcEntry is null)
        {
            await ApplyUnmatchedAsync(file, rdbSource, folderPath,
                discCrcValue is uint dc ? $"ディスクのCRC-32 {dc:X8} がRDBに無い" : "シリアル/CRCの対象ファイルを特定できない(.cueのデータトラック不明・.chd以外の圧縮形式 等)", rdb).ConfigureAwait(false);
        }

        ApplyJapaneseInfo(file, consoleId, discCrcValue, null, filePath);
    }

    /// <summary>ファイル内容全体のCRC-32を計算する。失敗した場合はnull。</summary>
    private static async Task<uint?> TryComputeFileCrcAsync(string filePath)
    {
        try
        {
            await using FileStream stream = File.OpenRead(filePath);
            return await Crc32.ComputeAsync(stream).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] CRC-32の計算に失敗しました({filePath}): {ex.Message}");
            ScanLog.Warn($"{Path.GetFileName(filePath)}: CRC-32を計算できませんでした: {ex.Message}");
            return null;
        }
    }

    /// <summary>libretro-thumbnailsのファイル名規則: &amp; * / : ` &lt; &gt; ? \ | " を"_"にする。</summary>
    private static string ReplaceLibretroThumbnailChars(string value)
    {
        const string chars = "&*/:`<>?\\|\"";
        return new string(value.Select(c => chars.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>任意の文字列をボックスアートの保存ファイル名(拡張子なし)に使える形にする
    /// (ファイル名として使えない文字を"_"に置き換える)。シリアル・ファイル名の両方で使う。</summary>
    private static string SanitizeForFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>
    /// CRC-32・シリアルのどちらでもRDBのエントリと一致しなかった場合に呼ぶ。この時点で
    /// file.Titleは<see cref="CreateBaseRomFile"/>が設定した「拡張子を除いたファイル名」の
    /// ままになっているので、それをRDBの"name"相当として扱い、ボックスアートの検索
    /// (<see cref="ResolveAndDownloadBoxArtAsync"/>)だけは行う(一致した場合と同様、
    /// 見つからなければダミー画像を生成する)。Description/Genre等、RDBの他のフィールドは
    /// 補える情報が無いため既定値のままにする。
    /// </summary>
    private static async Task ApplyUnmatchedAsync(
        RomFile file, RdbSourceInfo? rdbSource, string folderPath, string? reason = null, RdbDatabase? rdb = null)
    {
        // CRC/シリアルが合わなくても、ファイル名の「簡略キー」(空白・記号・括弧内を除いた小文字)が
        // RDBの名前と一致するなら、同じタイトルと推定してタイトル・説明・画像を補う
        // (短縮名のROMセット等。別ダンプのため「この版」とは限らない)。
        if (rdb is not null
            && !string.Equals(rdbSource?.MatchBy, "romName", StringComparison.OrdinalIgnoreCase)
            && rdb.TryFindByCompactName(file.Title, out RdbGameEntry? guessedEntry))
        {
            string key = SanitizeForFileName(file.Title);
            ScanLog.Guessed(file.FileName, guessedEntry.Name ?? file.Title, reason ?? "RDB/DATに該当なし");
            await ApplyRdbEntryAsync(file, guessedEntry, key, rdbSource, folderPath, guessed: true).ConfigureAwait(false);
            return;
        }

        ScanLog.Unrecognized(file.FileName, (reason ?? "RDB/DATに該当なし") + " (ファイル名をタイトルとして画像を探します)");
        await ResolveAndDownloadBoxArtAsync(file, SanitizeForFileName(file.Title), rdbSource, folderPath).ConfigureAwait(false);
    }

    /// <summary>
    /// RdbSourceInfo.MatchBy=="romName"のコンソール向け。ファイル(zip/7z等のアーカイブ全体、
    /// または生ファイル)を展開・解析せず、そのまま1ゲームの単位として扱う。拡張子を除いた
    /// ファイル名を、RDBの"rom_name"(同じく拡張子を除いた部分。<see cref="RdbDatabase.ByRomBaseName"/>)
    /// と比較して照合する。CRC計算・アーカイブの展開は一切行わないため、中身が複数ファイルで
    /// 構成されるアーケード系アーカイブ(FBNeo等)でも問題なく動く。
    /// 一致しなくても登録はする(ファイル名ベースのTitleのまま)。その場合もボックスアートの
    /// 検索(<see cref="ApplyUnmatchedAsync"/>)だけは行う。
    /// </summary>
    private static readonly Regex GdRomSerialPattern =
        new(@"^(GD[A-Z])-?(\d{4}[A-Z]?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryGetGdRomSerialFromName(string baseName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? serial)
    {
        Match m = GdRomSerialPattern.Match(baseName.Trim());
        serial = m.Success ? (m.Groups[1].Value + "-" + m.Groups[2].Value).ToUpperInvariant() : null;
        return m.Success;
    }

    private static async Task<RomFile?> BuildRomFileByNameAsync(
        string filePath, int parentFolderNo, string consoleId, RdbSourceInfo? rdbSource, string folderPath,
        RdbDatabase rdb)
    {
        if (!NativeFileId.TryGetFileIdentity(filePath, out ulong volumeSerial, out Guid fileId))
        {
            return null;
        }

        RomFile file = CreateBaseRomFile(filePath, parentFolderNo, fileId, volumeSerial, consoleId);

        string baseName = Path.GetFileNameWithoutExtension(filePath);
        if (rdb.TryFindByRomBaseName(baseName, out RdbGameEntry? entry))
        {
            await ApplyRdbEntryAsync(file, entry, SanitizeForFileName(baseName), rdbSource, folderPath)
                .ConfigureAwait(false);

            // DATのシリアル(Wii UのタイトルID等)があれば、日本語表(data/jdb/{コンソールId}.csv)のSerial列で引く。
            ApplyJapaneseInfo(file, consoleId, null, entry.Serial, filePath, baseName);
        }
        else if (TryGetGdRomSerialFromName(baseName, out string? gdSerial)
            && rdb.TryFindBySerial(gdSerial, out RdbGameEntry? gdEntry))
        {
            // GD-ROMのCHD(例: gds-0014.chd)は、ファイル名がそのままシリアル(GDS-0014)になっている。
            await ApplyRdbEntryAsync(file, gdEntry, SanitizeForFileName(gdSerial), rdbSource, folderPath)
                .ConfigureAwait(false);
            ScanLog.Info($"{Path.GetFileName(filePath)}: ファイル名のシリアル {gdSerial} で「{gdEntry.Name}」と認識しました");
            ApplyJapaneseInfo(file, consoleId, null, gdSerial, filePath, baseName);
        }
        else if (string.Equals(consoleId, "3ds", StringComparison.OrdinalIgnoreCase)
            && await TryApplyNintendo3dsProductCodeAsync(file, filePath, baseName, rdb, rdbSource, folderPath).ConfigureAwait(false))
        {
            ApplyJapaneseInfo(file, consoleId, null, null, filePath, baseName);
        }
        else if (string.Equals(consoleId, "wiiu", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(filePath).Equals(".wua", StringComparison.OrdinalIgnoreCase)
            && await TryApplyWiiUGameCodeAsync(file, filePath, baseName, rdb, rdbSource, folderPath).ConfigureAwait(false))
        {
            // ApplyJapaneseInfoはTryApplyWiiUGameCodeAsync内で、読み取ったゲームIDを渡して実行済み。
        }
        else if (await TryApplyArchiveEntryCrcAsync(file, filePath, baseName, rdb, rdbSource, folderPath).ConfigureAwait(false))
        {
            ApplyJapaneseInfo(file, consoleId, null, null, filePath, baseName);
        }
        else
        {
            // MAMEのセット名のファイル(例: cvs2.zip)は、日本語表(data/jdb/{コンソールId}.csv)のromname列で
            // 英語タイトルが分かれば、それをタイトルにして画像を探す(画像の取得は日本語表の適用より先に行うため)。
            // GD-ROMのCHD(gdl-0008等)でRDBに無いものは、ファイル名の品番でも日本語表を引く。
            string? unmatchedSerial = TryGetGdRomSerialFromName(baseName, out string? parsedSerial) ? parsedSerial : null;
            if (JapaneseGameDb.HasTable(consoleId))
            {
                JapaneseGameInfo tableInfo = new();
                bool tableHit = unmatchedSerial is not null
                    && JapaneseGameDb.TryFind(consoleId, null, unmatchedSerial, out tableInfo);
                tableHit = tableHit || JapaneseGameDb.TryFindByName(consoleId, baseName, out tableInfo);
                if (tableHit && tableInfo.EnglishTitle.Length > 0)
                {
                    file.Title = tableInfo.EnglishTitle;
                }
            }

            await ApplyUnmatchedAsync(file, rdbSource, folderPath, $"ROMセット名 \"{baseName}\" がRDB/DATに無い").ConfigureAwait(false);

            // DATに無いファイル名でも、日本語表にenname(英語タイトル)が合えば日本語情報を付ける。
            ApplyJapaneseInfo(file, consoleId, null, unmatchedSerial, filePath, baseName);
        }

        return file;
    }

    /// <summary>
    /// zip内に対象拡張子のエントリが1つでもあれば、そのエントリのCRC-32(zip自体が
    /// central directoryに持っている値。解凍し直さずそのまま使える)でRomFileを組み立てる。
    /// ディスク系のコンソールでは、先にzip内のイメージ(cueがあればそのデータトラック)の
    /// 先頭だけを展開してシリアルを読み取り、シリアルで照合する(CRC-32はフォールバック。
    /// その際は.cueではなくデータトラックのエントリのCRCを使う)。
    /// 対象拡張子のエントリが無いzipは、このコンソール用のROMではないと判断して
    /// 登録しない(null を返す)。
    /// </summary>
    private static async Task<RomFile?> BuildRomFileFromZipAsync(
        string filePath, HashSet<string> allowedExtensions, int parentFolderNo, string consoleId,
        RdbSourceInfo? rdbSource, string folderPath, RdbDatabase rdb)
    {
        if (!NativeFileId.TryGetFileIdentity(filePath, out ulong volumeSerial, out Guid fileId))
        {
            return null;
        }

        bool found;
        uint? crc;
        string? serial;
        string? romEntryName;
        long romEntrySize;
        try
        {
            // シリアル読み取りでエントリの一部を展開するため、スレッドプールで実行する。
            (found, crc, serial, romEntryName, romEntrySize) = await Task.Run(() =>
            {
                using ZipArchive archive = ZipFile.OpenRead(filePath);
                ZipArchiveEntry? romEntry = archive.Entries.FirstOrDefault(
                    e => allowedExtensions.Contains(Path.GetExtension(e.FullName)));

                if (romEntry is null)
                {
                    return (false, (uint?)null, (string?)null, (string?)null, 0L);
                }

                ZipArchiveEntry crcSourceEntry = romEntry;
                string? discSerial = null;
                if (DiscSerialReader.UsesSerial(consoleId))
                {
                    discSerial = DiscSerialReader.ReadSerialFromZip(consoleId, archive, out ZipArchiveEntry? dataEntry);
                    crcSourceEntry = dataEntry ?? romEntry;
                }

                // ZipArchiveEntry.Crc32はzip作成時に計算・central directoryへ保存された値を
                // そのまま返す(圧縮されたエントリを展開し直す必要が無い)。
                return (true, (uint?)unchecked((uint)crcSourceEntry.Crc32), discSerial, (string?)romEntry.FullName, romEntry.Length);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] zipの読み込みに失敗しました({filePath}): {ex.Message}");
            ScanLog.Warn($"{Path.GetFileName(filePath)}: zipを読み込めませんでした: {ex.Message}");
            return null;
        }

        if (!found)
        {
            return null;
        }

        RomFile file = CreateBaseRomFile(filePath, parentFolderNo, fileId, volumeSerial, consoleId);

        bool matched = false;

        if (serial is not null)
        {
            // シリアルが読み取れているコンソール(ディスク系)。シリアルで照合し、
            // 一致しなければ(=RDB未収録)、CRC-32へはフォールバックしない
            // (ディスクのCRC-32はダンプ方法で変わり、シリアル不一致からの照合には使えないため)。
            if (rdb.TryFindBySerial(serial, out RdbGameEntry? serialEntry))
            {
                await ApplyRdbEntryAsync(file, serialEntry, SanitizeForFileName(serial), rdbSource, folderPath)
                    .ConfigureAwait(false);
                matched = true;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RomFolderScanService] シリアルで一致しませんでした(serial={serial}): {filePath}");
            }
        }
        else if (crc is uint value)
        {
            // シリアルが読み取れなかった場合(カートリッジ系コンソール、またはディスク系でも
            // SYSTEM.CNFが無い等でシリアルが取れなかった場合)のみ、CRC-32で照合する。
            // 生のCRCで一致しなければ、ヘッダ除去・バイト順の正規化をしたCRCでもう一度照合する。
            if (!rdb.ByCrc.TryGetValue(value, out RdbGameEntry? crcEntry)
                && romEntryName is not null && RomNormalizer.MayNeedNormalization(consoleId))
            {
                uint? normalizedCrc = await Task.Run(
                    () => TryComputeNormalizedEntryCrc(consoleId, filePath, romEntryName, romEntrySize)).ConfigureAwait(false);
                if (normalizedCrc is uint normalized)
                {
                    value = normalized;
                    crc = normalized;
                    rdb.ByCrc.TryGetValue(normalized, out crcEntry);
                }
            }

            if (crcEntry is not null)
            {
                await ApplyRdbEntryAsync(file, crcEntry, $"{value:X8}", rdbSource, folderPath).ConfigureAwait(false);
                matched = true;
            }
        }

        if (!matched && serial is null && romEntryName is not null
            && await TryApplyGameCodeAsync(file, consoleId, filePath, romEntryName, rdb, rdbSource, folderPath).ConfigureAwait(false))
        {
            matched = true;
        }

        if (!matched)
        {
            // CRC-32・シリアルのどちらでも一致しなかった。ボックスアートだけは、
            // ファイル名をRDBの"name"相当とみなして検索を試みる。
            string zipReason = serial is not null
                ? $"シリアル {serial} がRDBに無い"
                : crc is uint zc ? $"zip内ファイル「{romEntryName}」({romEntrySize:N0}バイト)のCRC-32 {zc:X8} がRDBに無い(ヘッダ除去・並び補正後も不一致、または対象外の機種)"
                : "CRC-32を取得できない";
            await ApplyUnmatchedAsync(file, rdbSource, folderPath, zipReason, rdb).ConfigureAwait(false);
        }

        ApplyJapaneseInfo(file, consoleId, crc, serial, filePath);

        return file;
    }

    /// <summary>
    /// 7z内に対象拡張子のエントリがあれば、そのエントリのCRC-32(7zのヘッダに記録された値。
    /// 展開せずに読める)でRomFileを組み立てる。生のCRCで一致しなければ、ヘッダ除去・
    /// バイト順の正規化をしたCRCでもう一度照合する。
    /// 7zはカートリッジ系のみ対応する(ディスク系はシリアルを読まず、ファイル名ベースのまま登録する)。
    /// 対象拡張子のエントリが無い7zは、このコンソール用のROMではないと判断して登録しない(nullを返す)。
    /// </summary>
    private static async Task<RomFile?> BuildRomFileFromSevenZipAsync(
        string filePath, HashSet<string> allowedExtensions, int parentFolderNo, string consoleId,
        RdbSourceInfo? rdbSource, string folderPath, RdbDatabase rdb)
    {
        if (!NativeFileId.TryGetFileIdentity(filePath, out ulong volumeSerial, out Guid fileId))
        {
            return null;
        }

        ArchiveEntryInfo? romEntry;
        try
        {
            IReadOnlyList<ArchiveEntryInfo> entries = await Task.Run(() => ArchiveReader.ListEntries(filePath)).ConfigureAwait(false);
            romEntry = entries.FirstOrDefault(e => allowedExtensions.Contains(Path.GetExtension(e.FullName)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] 7zの読み込みに失敗しました({filePath}): {ex.Message}");
            ScanLog.Warn($"{Path.GetFileName(filePath)}: 7zを読み込めませんでした: {ex.Message}");
            return null;
        }

        if (romEntry is null)
        {
            return null;
        }

        RomFile file = CreateBaseRomFile(filePath, parentFolderNo, fileId, volumeSerial, consoleId);

        uint? crc = romEntry.Crc;
        RdbGameEntry? entry = null;
        if (!DiscSerialReader.IsDiscBased(consoleId))
        {
            if (!rdb.ByCrc.TryGetValue(romEntry.Crc, out entry) && RomNormalizer.MayNeedNormalization(consoleId))
            {
                uint? normalizedCrc = await Task.Run(
                    () => TryComputeNormalizedEntryCrc(consoleId, filePath, romEntry.FullName, romEntry.Size)).ConfigureAwait(false);
                if (normalizedCrc is uint normalized)
                {
                    crc = normalized;
                    rdb.ByCrc.TryGetValue(normalized, out entry);
                }
            }
        }

        if (entry is not null && crc is uint matchedCrc)
        {
            await ApplyRdbEntryAsync(file, entry, $"{matchedCrc:X8}", rdbSource, folderPath).ConfigureAwait(false);
        }
        else if (DiscSerialReader.IsDiscBased(consoleId)
            || !await TryApplyGameCodeAsync(file, consoleId, filePath, romEntry.FullName, rdb, rdbSource, folderPath).ConfigureAwait(false))
        {
            await ApplyUnmatchedAsync(file, rdbSource, folderPath,
                DiscSerialReader.IsDiscBased(consoleId)
                    ? "7zのディスク系は識別に対応していない(ファイル名のみ)"
                    : $"7z内ファイル「{romEntry.FullName}」({romEntry.Size:N0}バイト)のCRC-32 {romEntry.Crc:X8} がRDBに無い(ヘッダ除去・並び補正後も不一致、または対象外の機種)", rdb).ConfigureAwait(false);
        }

        ApplyJapaneseInfo(file, consoleId, DiscSerialReader.IsDiscBased(consoleId) ? null : crc, null, filePath);
        return file;
    }

    /// <summary>
    /// data/jdb/{コンソールId}.csv(<see cref="JapaneseGameDb"/>)に載っているゲームだけ、
    /// 日本語タイトル・ひらがな読みを持たせ、概要・ジャンル・発売元・発売年・プレイ人数を日本語表の内容で上書きする
    /// (英語タイトル file.Title は変えない)。表が無いコンソールでは何もしない(コンソールを増やすときは、
    /// 同じ形式のCSVを data/jdb/{コンソールId}.csv に置くだけでよい)。
    /// 照合はCRC-32を先に、無ければシリアル(ディスク系のシリアル、N64のゲームコード)で行う。
    /// N64でどちらも渡されていない/一致しなければ、ROMのヘッダーからゲームコードを読み直して探す(.v64/.n64対応)。
    /// ボックスアートの検索は英語名(file.Title)を使うので、必ずその取得が済んだ後に呼ぶこと。
    /// </summary>
    private static void ApplyJapaneseInfo(RomFile file, string consoleId, uint? crc, string? serial, string filePath, string? nameHint = null)
    {
        if (!JapaneseGameDb.HasTable(consoleId)) return;

        if (!JapaneseGameDb.TryFind(consoleId, crc, serial, out JapaneseGameInfo info))
        {
            // ファイル名で照合するコンソール(Wii U等)は、CSVのenname列とファイル名で探す。
            if (JapaneseGameDb.TryFindByName(consoleId, nameHint, out info))
            {
                // 見つかった
            }
            else if (!string.Equals(consoleId, "n64", StringComparison.OrdinalIgnoreCase)) return;
            else
            {
                string? gameCode = TryReadN64GameCode(filePath);
                if (gameCode is null || !JapaneseGameDb.TryFind(consoleId, null, gameCode, out info)) return;
            }
        }

        // 英語タイトル(file.Title)は残し、日本語タイトルとひらがな読みは別の列へ持つ。
        // 概要・ジャンル・発売元・発売年・人数は、この表のほうが優先(RDBの値を上書きする)。
        // 英語タイトルはRDBが優先。RDBに名前が無かったゲーム(ファイル名のままのもの)だけ、表の英語タイトルで補う。
        if (!file.TitleFromRdb && info.EnglishTitle.Length > 0) file.Title = info.EnglishTitle;
        file.TitleJa = info.Title;
        file.TitleKana = info.Kana;
        if (info.Description.Length > 0) file.Description = info.Description;
        if (info.Genre.Length > 0) file.Genre = info.Genre;
        if (info.Publisher.Length > 0) file.Publisher = info.Publisher;
        if (info.ReleaseYear is int year) file.ReleaseYear = year;
        if (info.Players.Length > 0) file.Players = info.Players;
    }

    /// <summary>
    /// N64のROM(.z64/.v64/.n64、またはそれを含むzip)の先頭64バイトから、ゲームコード(0x3B〜0x3E。例: "NSMJ")を読む。
    /// バイト並びは先頭4バイトで判定し、z64並びに直してから読む。N64のROMでなければnull。
    /// </summary>
    private static string? TryReadN64GameCode(string filePath)
    {
        try
        {
            byte[] head = new byte[0x40];
            int read;
            if (string.Equals(Path.GetExtension(filePath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                using ZipArchive archive = ZipFile.OpenRead(filePath);
                ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
                {
                    string ext = Path.GetExtension(e.FullName);
                    return ext.Equals(".z64", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".v64", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".n64", StringComparison.OrdinalIgnoreCase);
                });
                if (entry is null) return null;
                using Stream stream = entry.Open();
                read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }
            else
            {
                using FileStream stream = File.OpenRead(filePath);
                read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }

            if (read < head.Length) return null;

            // z64: 80 37 12 40 / v64(2バイトずつ入れ替え): 37 80 40 12 / n64(4バイト単位で逆順): 40 12 37 80
            if (head[0] == 0x37 && head[1] == 0x80)
            {
                for (int i = 0; i < head.Length; i += 2) (head[i], head[i + 1]) = (head[i + 1], head[i]);
            }
            else if (head[0] == 0x40 && head[1] == 0x12)
            {
                for (int i = 0; i < head.Length; i += 4)
                {
                    (head[i], head[i + 3]) = (head[i + 3], head[i]);
                    (head[i + 1], head[i + 2]) = (head[i + 2], head[i + 1]);
                }
            }
            else if (!(head[0] == 0x80 && head[1] == 0x37))
            {
                return null;
            }

            string code = System.Text.Encoding.ASCII.GetString(head, 0x3B, 4);
            return code.All(char.IsAsciiLetterOrDigit) ? code : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] N64のゲームコード読み取りに失敗しました({filePath}): {ex.Message}");
            return null;
        }
    }

    /// <summary>RDB照合前の、既定値だけのRomFileを組み立てる(Titleは拡張子を除いたファイル名)。</summary>
    private static RomFile CreateBaseRomFile(
        string filePath, int parentFolderNo, Guid fileId, ulong volumeSerial, string consoleId) => new()
        {
            ParentId = parentFolderNo,
            FileId = fileId,
            VolumeSerial = volumeSerial,
            FileName = Path.GetFileName(filePath),
            ConsoleId = consoleId,
            Title = Path.GetFileNameWithoutExtension(filePath),
            IsDeleted = false,
        };

    /// <summary>
    /// 照合できたRDBのエントリ(CRC-32またはシリアルで一致したもの)を、Title/Description/
    /// Genre/ReleaseYear/Publisher/Playersへ反映し、ボックスアートのダウンロード
    /// (失敗時のタイトル置換リトライ込み)も行う。boxArtKeyはボックスアートの保存ファイル名
    /// (拡張子なし)で、CRC-32で一致した場合は8桁16進数、シリアルで一致した場合はシリアル
    /// (例: "SLUS-00908")。
    /// RDBのdeveloperはRomFile.Publisherへ対応させている(RDBには別途publisherという
    /// 項目もあるが、developerを採用する)。
    ///
    /// ボックスアートの取得結果(実際に取れたファイル名)をfile.IconPathへ正しく反映する
    /// 必要があるため、ここは(呼び出し側含めて)結果を待つ形にしている。ダウンロード自体は
    /// DownloadManagerが最大5並列にスロットリングするので、フォルダ全体のスキャンが
    /// 極端に遅くなることはない。
    /// </summary>
    private static async Task ApplyRdbEntryAsync(
        RomFile file, RdbGameEntry entry, string boxArtKey, RdbSourceInfo? rdbSource, string folderPath,
        bool guessed = false)
    {
        if (!string.IsNullOrEmpty(entry.Name))
        {
            file.Title = entry.Name;
            file.TitleFromRdb = true;
        }

        if (!guessed) ScanLog.Recognized(file.FileName, file.Title, boxArtKey);

        if (!string.IsNullOrEmpty(entry.Description)) file.Description = entry.Description;
        if (!string.IsNullOrEmpty(entry.Genre)) file.Genre = entry.Genre;
        if (entry.ReleaseYear is int year) file.ReleaseYear = year;
        if (!string.IsNullOrEmpty(entry.Developer)) file.Publisher = entry.Developer;
        if (!string.IsNullOrEmpty(entry.Users)) file.Players = entry.Users;

        // libretro-thumbnailsの画像は、RDBのname("(Unl)"や"[b]"等の付記が落ちていることがある)ではなく、
        // rom_name(拡張子を除く)と同じ名前で置かれている。ROMセット名で照合するコンソール
        // (アーケード系。rom_nameが"1941.zip"のような短い名前)では使わない。
        string? preferredImageName = null;
        if (!string.Equals(rdbSource?.MatchBy, "romName", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entry.RomName))
        {
            preferredImageName = Path.GetFileNameWithoutExtension(entry.RomName);
        }

        await ResolveAndDownloadBoxArtAsync(file, boxArtKey, rdbSource, folderPath, preferredImageName).ConfigureAwait(false);
    }

    /// <summary>
    /// RDBのTitleと実際のlibretro-thumbnailsのファイル名が微妙に食い違うことがある
    /// (例: RDBでは"(J)"だが実際のファイルは"(Japan)")。ここに置換パターンを足すだけで、
    /// ダウンロード失敗時のリトライ候補を増やせる。上から順に(元のタイトルへ)適用を試す。
    /// </summary>
    private static readonly (string From, string To)[] BoxArtTitleRetryPatterns =
    {
        ("(J)", "(Japan)"),
    };

    /// <summary>
    /// rdblist.jsonのImage(ボックスアートのベースURL)が設定されていれば、Titleベースの
    /// URL候補("{Title}.png"、取れなければ<see cref="BoxArtTitleRetryPatterns"/>で
    /// 置換した候補)を順に試してダウンロードする。保存先は
    /// "&lt;フォルダ&gt;\RetroNexus\Named_Boxarts\{boxArtKey}.png"にする。boxArtKeyは
    /// CRC-32で照合した場合は8桁16進数、シリアルで照合した場合はシリアル(例: "SLUS-00908")
    /// (URL探索にはTitleを使うが、保存するファイル名自体はCRC-32/シリアルベースにしておくことで、
    /// 後でRDBのTitle表記が変わっても同じ画像ファイルを使い続けられ、わざわざ元のタイトルと
    /// 同じファイル名にする必要も無くなる)。
    /// rdblist.jsonのImageList("list")からlibretro-thumbnails側の実ファイル一覧
    /// (ファイル名 → <see cref="ImageListEntry"/>)が取得できている場合は、
    /// 候補ファイル名でその一覧を引き、
    /// ・見つからなければ実在しないと確定しているのでアクセスせずスキップする
    /// ・見つかった場合はrdbSource.Imageから自前でURLを組み立てるのではなく、
    /// 　一覧が持つURL情報(ImageListEntry.DirectUrl、無ければBlobApiUrl)を
    /// 　そのまま使ってダウンロードする(自前でパーセントエンコードすると、
    /// 　"["・"]"等の記号周りでGitHub実際のURLと食い違って404になることがあるため、
    /// 　GitHubが返した/生成できる正しいURLをそのまま信用する)。
    /// 一覧が取得できない場合は従来通り、rdbSource.Imageから組み立てたURLへ直接
    /// アクセスして確認する(スペースだけを%20に置換する簡易エンコード)。
    /// 上記の完全一致候補がどれも見つからなかった場合、一覧が取得できていれば
    /// <see cref="BoxArtTitleMatcher"/>による名寄せを試みる。libretro-thumbnailsの
    /// ファイル名は丸カッコの中身(リージョン・言語表記)が一貫していない
    /// ("(Japan)""(J)""(Ja)""(Japan, USA)""(Ja, En)"等)ため、丸カッコの外側の
    /// 基本タイトルが完全一致するファイルの中から、タグが最も近いものを選ぶ
    /// (Demo/Proto/Beta等は、探している側と有無が一致しない候補を除外する)。
    /// 一覧が無い場合は、名寄せに使える実ファイル名の集合が無いため行わない。
    /// ダウンロードに成功した場合はfile.IconPathへそのファイル名("{boxArtKey}.png")を設定する。
    /// どの候補も「実在しない」と確定した場合(一覧に存在しない/404)は、同じ保存先へ
    /// <see cref="BoxArtPlaceholderGenerator"/>で「グレー背景+テキスト」のダミーPNGを生成し、
    /// IconPathもそのダミーを指すようにする。ダミーには理由・タイトル・差し替え用のファイル名・
    /// 探したファイル名を描いてあるので、後から自分で画像を用意する際に何をどの名前で置けば
    /// よいかが分かる(同名のPNGで上書きすれば差し替わる)。
    /// 一方、レート制限(429/403)・5xx・タイムアウト等の一時的な失敗が1回でもあった場合は、
    /// ダミーを作らず何も保存しない(ダミーを置くと以後取得済み扱いになり、二度と取りに
    /// 行かなくなるため)。
    /// 保存先にファイルが既にあれば、それがダミーであっても取得済みとみなして何もしない
    /// (ダミーを本物にしたい場合は、手動で差し替えるか削除してから再スキャンする)。
    /// ユーザーが自分で置いた・差し替えた画像を上書きしないことを最優先にしているため、
    /// 既存ファイルを取り直す・上書きする処理はここには一切入れないこと。
    /// ダミーの生成自体に失敗した場合はIconPathを空文字のままにする
    /// (ゲーム選択画面側の既存のフォールバック表示がそのまま機能する)。
    /// </summary>
    private static async Task ResolveAndDownloadBoxArtAsync(
        RomFile file, string boxArtKey, RdbSourceInfo? rdbSource, string folderPath, string? preferredImageName = null)
    {
        if (string.IsNullOrEmpty(rdbSource?.Image) || string.IsNullOrEmpty(file.Title))
        {
            ScanLog.Art(ScanLog.ArtResult.None, file.Title, "画像のURL(rdblist.jsonのimage)が未設定、またはタイトルが空");
            return;
        }

        string localFileName = $"{boxArtKey}.png";
        string destinationPath = Path.Combine(folderPath, "RetroNexus", "Named_Boxarts", localFileName);

        if (File.Exists(destinationPath))
        {
            // 同じCRC/シリアルのファイルが既にある(ダウンロード済み・ユーザーが置いた画像・
            // 自動生成したダミーのいずれでも区別しない)。再取得せずそのまま使う。
            file.IconPath = localFileName;
            ScanLog.Art(ScanLog.ArtResult.Existing, file.Title, $"既存の画像を使用: {localFileName}");
            return;
        }

        var candidateTitles = new List<string> { file.Title };
        foreach ((string from, string to) in BoxArtTitleRetryPatterns)
        {
            if (file.Title.Contains(from, StringComparison.Ordinal))
            {
                candidateTitles.Add(file.Title.Replace(from, to, StringComparison.Ordinal));
            }
        }

        // libretro-thumbnailsは、ファイル名に使えない文字(& * / : ` < > ? \ | ")を"_"に置き換えて保存している
        // (例: "Zelda: Collector's Edition" → "Zelda_ Collector's Edition")。置換した候補も試す。
        foreach (string candidate in candidateTitles.ToList())
        {
            string replaced = ReplaceLibretroThumbnailChars(candidate);
            if (!candidateTitles.Contains(replaced, StringComparer.Ordinal))
            {
                candidateTitles.Add(replaced);
            }
        }

        // 実ファイル一覧が取得できていれば、それを使って存在確認とダウンロードURLの
        // 特定を行う。取得できなかった場合はnullのままにしておき、以下のループでは
        // 従来通り全候補にURLを組み立ててアクセスするフォールバックになる。
        Dictionary<string, ImageListEntry>? imageEntries = await RdbCatalogService.GetImageEntriesAsync(rdbSource).ConfigureAwait(false);
        if (imageEntries is null)
        {
            ScanLog.Once("imagelist", string.IsNullOrEmpty(rdbSource.ImageList)
                ? "画像の一覧(rdblist.jsonのlist)が未設定のため、URLを直接組み立てて取得します。"
                : "画像の一覧を取得できませんでした(GitHub APIの制限・通信エラー等)。URLを直接組み立てて取得します。");
        }

        // rom_name(拡張子を除く)を最優先の候補にする。一覧が取得できているときだけ(一覧が無いと
        // 候補ごとにネットワークアクセスが増えてしまうため)。
        if (imageEntries is not null
            && !string.IsNullOrWhiteSpace(preferredImageName)
            && !candidateTitles.Contains(preferredImageName, StringComparer.Ordinal))
        {
            candidateTitles.Insert(0, preferredImageName);
        }

        // 一覧上に存在した候補が1つでもあったか(ダミー画像に描く理由の判定用)。
        bool foundInImageList = false;

        // 404以外の失敗(レート制限・通信エラー等)が1回でもあったか。あった場合は
        // 「実在しない」とは確定できないので、ダミー画像を作らない(後述)。
        bool hadTransientFailure = false;

        // libretro-thumbnails側で探したファイル名(ダミー画像に、後から何を置けばよいかの
        // 参考として描く)。名寄せで見つかった候補があれば、これにも追記する。
        string imageExt = string.IsNullOrEmpty(rdbSource.ImageExtension) ? ".png" : rdbSource.ImageExtension;
        List<string> remoteFileNames = candidateTitles.Select(t => $"{t}{imageExt}").ToList();

        foreach (string candidateTitle in candidateTitles)
        {
            string remoteFileName = $"{candidateTitle}{imageExt}";

            if (imageEntries is not null)
            {
                if (!imageEntries.TryGetValue(remoteFileName, out ImageListEntry? entry))
                {
                    // 一覧に存在しないことが確定しているので、アクセスせずスキップする。
                    continue;
                }

                foundInImageList = true;

                DownloadResult listResult = await DownloadBoxArtUsingListEntryAsync(rdbSource, remoteFileName, entry, destinationPath).ConfigureAwait(false);
                if (listResult == DownloadResult.Success)
                {
                    file.IconPath = localFileName;
                    OrientDownloadedBoxArt(destinationPath, rdbSource, file.Title);
                    ScanLog.Art(ScanLog.ArtResult.Downloaded, file.Title, $"取得: {remoteFileName} → {localFileName}");
                    return;
                }

                ScanLog.Warn($"{file.Title}: 画像一覧にはあるがダウンロードに失敗しました({listResult}): {remoteFileName} / URL={(string.IsNullOrEmpty(entry.DirectUrl) ? entry.BlobApiUrl : entry.DirectUrl)}");
                hadTransientFailure |= listResult == DownloadResult.Failed;

                // 一覧には存在したがダウンロードに失敗した(ネットワークエラー等)。
                // 次の候補タイトルへ進む(自前URL組み立てへのフォールバックはしない。
                // 一覧上は存在が確認できているファイルなので、組み立てたURLで再度
                // 試しても改善する見込みが薄いため)。
                continue;
            }

            // libretro-thumbnailsの実際のURLは、ファイル名中のスペースだけを%20に置き換え、
            // 丸カッコ等はそのままにする形式になっている(例: "Choro Q 64 (Japan).png" →
            // ".../Choro%20Q%2064%20(Japan).png")。
            string encodedRemoteFileName = remoteFileName.Replace(" ", "%20");
            string boxArtUrl = rdbSource.Image.TrimEnd('/') + "/" + encodedRemoteFileName;

            DownloadResult result = await DownloadManager.DownloadFileWithResultAsync(boxArtUrl, destinationPath).ConfigureAwait(false);
            if (result == DownloadResult.Success)
            {
                file.IconPath = localFileName;
                OrientDownloadedBoxArt(destinationPath, rdbSource, file.Title);
                ScanLog.Art(ScanLog.ArtResult.Downloaded, file.Title, $"取得: {remoteFileName} → {localFileName}");
                return;
            }

            hadTransientFailure |= result == DownloadResult.Failed;
        }

        // GD-ROMのシリアル(GDS-0014等)が保存名の場合、libretro-thumbnails側は
        // "Beach Spikers (GDS-0014).png" のように品番を括弧で付けているので、それで探す。
        if (imageEntries is not null && GdRomSerialPattern.IsMatch(boxArtKey))
        {
            string serialTag = "(" + boxArtKey + ")";
            string? serialFileName = imageEntries.Keys.FirstOrDefault(
                k => k.Contains(serialTag, StringComparison.OrdinalIgnoreCase));
            if (serialFileName is not null && imageEntries.TryGetValue(serialFileName, out ImageListEntry? serialEntry))
            {
                foundInImageList = true;
                remoteFileNames.Add($"{serialFileName} (品番で一致)");

                DownloadResult serialResult = await DownloadBoxArtUsingListEntryAsync(rdbSource, serialFileName, serialEntry, destinationPath).ConfigureAwait(false);
                if (serialResult == DownloadResult.Success)
                {
                    file.IconPath = localFileName;
                    OrientDownloadedBoxArt(destinationPath, rdbSource, file.Title);
                    ScanLog.Art(ScanLog.ArtResult.Downloaded, file.Title, $"品番で取得: {serialFileName} → {localFileName}");
                    return;
                }

                hadTransientFailure |= serialResult == DownloadResult.Failed;
            }
        }

        // ここまでの完全一致候補(元のTitleおよびBoxArtTitleRetryPatternsでの置換)では
        // 見つからなかった。実ファイル一覧が取得できていれば、丸カッコの中身(リージョン・
        // 言語表記の違い)を無視した名寄せを試みる(BoxArtTitleMatcher)。基本タイトル
        // (丸カッコの外側)が完全一致するファイルの中から、タグの近さで最も近いものを選ぶ。
        if (imageEntries is not null && !hadTransientFailure)
        {
            Dictionary<string, List<string>>? baseTitleIndex =
                await RdbCatalogService.GetBaseTitleIndexAsync(rdbSource).ConfigureAwait(false);
            Dictionary<string, List<string>>? compactIndex =
                await RdbCatalogService.GetCompactTitleIndexAsync(rdbSource).ConfigureAwait(false);

            // 探す名前: rom_name(あれば)→タイトル。まず基本タイトルの完全一致、無ければ簡略キー
            // (空白・記号の有無だけが違う名前。例: "aisenshinicol" → "Ai Senshi Nicol (Japan)")。
            string? fuzzyFileName = null;
            string fuzzyKind = "名寄せ";
            foreach (string name in candidateTitles)
            {
                fuzzyFileName = baseTitleIndex is not null ? BoxArtTitleMatcher.FindBestMatch(name, baseTitleIndex) : null;
                if (fuzzyFileName is not null) break;
            }

            if (fuzzyFileName is null && compactIndex is not null)
            {
                foreach (string name in candidateTitles)
                {
                    fuzzyFileName = BoxArtTitleMatcher.FindBestMatchCompact(name, compactIndex);
                    if (fuzzyFileName is not null) { fuzzyKind = "簡略名で名寄せ"; break; }
                }
            }

            if (fuzzyFileName is not null && imageEntries.TryGetValue(fuzzyFileName, out ImageListEntry? fuzzyEntry))
            {
                foundInImageList = true;
                remoteFileNames.Add($"{fuzzyFileName} ({fuzzyKind})");

                DownloadResult fuzzyResult = await DownloadBoxArtUsingListEntryAsync(rdbSource, fuzzyFileName, fuzzyEntry, destinationPath).ConfigureAwait(false);
                if (fuzzyResult == DownloadResult.Success)
                {
                    file.IconPath = localFileName;
                    OrientDownloadedBoxArt(destinationPath, rdbSource, file.Title);
                    ScanLog.Art(ScanLog.ArtResult.Downloaded, file.Title, $"{fuzzyKind}で取得: {fuzzyFileName} → {localFileName}");
                    return;
                }

                ScanLog.Warn($"{file.Title}: 名寄せした画像のダウンロードに失敗しました({fuzzyResult}): {fuzzyFileName} / URL={(string.IsNullOrEmpty(fuzzyEntry.DirectUrl) ? fuzzyEntry.BlobApiUrl : fuzzyEntry.DirectUrl)}");
                hadTransientFailure |= fuzzyResult == DownloadResult.Failed;
            }
        }

        if (hadTransientFailure)
        {
            // レート制限・通信エラー等、一時的な理由で取れなかった可能性がある。ここでダミーを
            // 保存すると「ファイルがある=取得済み」として以後二度と取りに行かなくなるため、
            // 何も保存しない(IconPathは空のまま。次回のスキャンで改めて取得を試みる)。
            System.Diagnostics.Debug.WriteLine(
                $"[RomFolderScanService] ボックスアートを一時的に取得できなかったため、ダミー画像は作りません: {file.Title}");
            ScanLog.Art(ScanLog.ArtResult.Deferred, file.Title,
                "レート制限・通信エラー等で取得できませんでした。ダミーは作らず、次回の再スキャンで再試行します。探した名前: "
                + string.Join(" / ", remoteFileNames));
            return;
        }

        // どの候補も「実在しない」と確定した(一覧に無い/404)。後から何をどのファイル名で
        // 用意すればよいか分かるよう、保存先にダミー画像を生成する。
        BoxArtPlaceholderReason reason = imageEntries is null
            ? BoxArtPlaceholderReason.FetchFailedWithoutList
            : foundInImageList
                ? BoxArtPlaceholderReason.DownloadFailed
                : BoxArtPlaceholderReason.NotInImageList;

        if (BoxArtPlaceholderGenerator.TryCreate(destinationPath, file.Title, localFileName, remoteFileNames, reason))
        {
            file.IconPath = localFileName;
            string reasonText = reason switch
            {
                BoxArtPlaceholderReason.NotInImageList => "画像一覧に該当する名前が無い",
                BoxArtPlaceholderReason.DownloadFailed => "一覧にはあるがダウンロードできない",
                BoxArtPlaceholderReason.FetchFailedWithoutList => "画像一覧を取得できず、直接取得も失敗",
                _ => reason.ToString(),
            };
            ScanLog.Art(ScanLog.ArtResult.Placeholder, file.Title,
                $"ダミー画像を作成 ({localFileName}) 理由: {reasonText} / 探した名前: {string.Join(" / ", remoteFileNames)}");
        }
        else
        {
            ScanLog.Art(ScanLog.ArtResult.None, file.Title, $"ダミー画像の作成に失敗 ({localFileName})");
        }
    }

    /// <summary>
    /// ダウンロード直後の画像の向きを、コンソールの基本の向き(rdblist.jsonのboxArtOrientation)に
    /// そろえる(明らかに逆のときだけ回転する)。回転した場合はログに残す。
    /// </summary>
    private static void OrientDownloadedBoxArt(string path, RdbSourceInfo? rdbSource, string title)
    {
        string? note = BoxArtOrienter.TryOrient(path, rdbSource?.BoxArtOrientation, rdbSource?.BoxArtRotation);
        if (note is not null) ScanLog.Info($"{title}: {note}");
    }

    /// <summary>
    /// 一覧(GitHubのgitツリー)にあるのにthumbnails.libretro.com側が404を返すファイルがある
    /// (サイトの同期遅れ。一覧と実体がずれている)。その場合はGitHubのraw URLから取り直す。
    /// </summary>
    private static async Task<DownloadResult> DownloadWithGitHubRawFallbackAsync(
        RdbSourceInfo rdbSource, string remoteFileName, string url, string destinationPath)
    {
        DownloadResult result = await DownloadManager.DownloadFileWithResultAsync(url, destinationPath).ConfigureAwait(false);
        if (result == DownloadResult.Success) return result;

        string? rawUrl = BuildGitHubRawUrl(rdbSource.ImageList, remoteFileName);
        if (rawUrl is null || string.Equals(rawUrl, url, StringComparison.Ordinal)) return result;

        DownloadResult rawResult = await DownloadManager.DownloadFileWithResultAsync(rawUrl, destinationPath).ConfigureAwait(false);
        if (rawResult == DownloadResult.Success)
        {
            ScanLog.Info($"{remoteFileName}: thumbnails.libretro.comに無かったため、GitHubから取得しました");
            return rawResult;
        }

        return result;
    }

    private static readonly Regex GitHubTreeListPattern = new(
        @"repos/(?<owner>[^/]+)/(?<repo>[^/]+)/git/trees/(?<ref>[^:/]+):(?<folder>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>rdblist.jsonの"list"(Trees API形式)から、GitHubのraw URLを組み立てる。形式が違えばnull。</summary>
    private static string? BuildGitHubRawUrl(string? imageListUrl, string fileName)
    {
        if (string.IsNullOrEmpty(imageListUrl)) return null;
        Match m = GitHubTreeListPattern.Match(imageListUrl);
        if (!m.Success) return null;
        return $"https://raw.githubusercontent.com/{m.Groups["owner"].Value}/{m.Groups["repo"].Value}/{m.Groups["ref"].Value}/{m.Groups["folder"].Value.Trim('/')}/{Uri.EscapeDataString(fileName)}";
    }

    /// <summary>
    /// 実ファイル一覧から見つかった<see cref="ImageListEntry"/>を使ってボックスアートを
    /// ダウンロードする。DirectUrl(GitHub Contents APIの"download_url"。そのままGETすれば
    /// 生バイト列が返ってくる)があればそれを優先し、無ければBlobApiUrl(Git Blob API)に
    /// Accept: application/vnd.github.raw+jsonを指定してリクエストし、生バイナリを
    /// そのまま取得する。どちらも無ければfalseを返す。
    /// </summary>
    private static Task<DownloadResult> DownloadBoxArtUsingListEntryAsync(
        RdbSourceInfo rdbSource, string remoteFileName, ImageListEntry entry, string destinationPath)
    {
        // 一括取得済みなら、キャッシュからコピーする(ネットワークには出ない)。
        if (rdbSource.Bulk)
        {
            string cached = Path.Combine(RdbCatalogService.GetBulkImageFolder(rdbSource), remoteFileName);
            if (File.Exists(cached))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(cached, destinationPath, overwrite: true);
                    return Task.FromResult(DownloadResult.Success);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RomFolderScanService] キャッシュ画像のコピーに失敗: {ex.Message}");
                    ScanLog.Warn($"キャッシュ画像のコピーに失敗: {remoteFileName}: {ex.Message}");
                }
            }
        }

        if (!string.IsNullOrEmpty(entry.DirectUrl))
        {
            return DownloadWithGitHubRawFallbackAsync(rdbSource, remoteFileName, entry.DirectUrl, destinationPath);
        }

        if (!string.IsNullOrEmpty(entry.BlobApiUrl))
        {
            return DownloadManager.DownloadFileWithResultAsync(
                entry.BlobApiUrl,
                destinationPath,
                DownloadManager.GitHubRawBlobAcceptMediaType);
        }

        // URL情報が無いエントリは取りようが無いので、「実在しない」と同じ扱いにする。
        return Task.FromResult(DownloadResult.NotFound);
    }
}