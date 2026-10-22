using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using EmulatorLauncher.Models;
using Microsoft.Data.Sqlite;

namespace EmulatorLauncher.Storage;

/// <summary>
/// RomFileテーブル(RomFolder配下で見つかった1本1本のROMファイルの管理情報)への
/// 非同期CRUDアクセス。設計方針・接続の扱い方は<see cref="RomFolderRepository"/>と同じで、
/// DBファイルの場所・接続文字列・PRAGMA設定・スキーマ作成は<see cref="DatabaseManager"/>が
/// 一元管理しており、このクラスは<see cref="DatabaseManager.OpenConnectionAsync"/>を通じて
/// 得た接続でSQLを実行するだけ(SqliteConnectionを自前でnewしない)。
/// </summary>
public static class RomFileRepository
{
    // ======================================================================
    // Dapper用の行マッピングDTO(RomFolderRepositoryと同じ考え方:
    // Guid/ulong/DateTimeOffset/boolをTEXT/INTEGERへ明示的に変換してから読み書きする)。
    // ======================================================================
    private sealed class RomFileRow
    {
        public int No { get; set; }
        public int ParentId { get; set; }
        public string FileId { get; set; } = string.Empty;
        public string VolumeSerial { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long IsDeleted { get; set; }
        public string ConsoleId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? TitleJa { get; set; }
        public string? TitleKana { get; set; }
        public string Publisher { get; set; } = string.Empty;
        public int ReleaseYear { get; set; }
        public string Players { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? LastPlayed { get; set; }
        public int PlayTimeMinutes { get; set; }
        public string IconPath { get; set; } = string.Empty;
        public string? Custom { get; set; }
    }

    private static RomFile ToModel(RomFileRow row) => new()
    {
        No = row.No,
        ParentId = row.ParentId,
        FileId = Guid.Parse(row.FileId),
        VolumeSerial = ulong.Parse(row.VolumeSerial, CultureInfo.InvariantCulture),
        FileName = row.FileName,
        IsDeleted = row.IsDeleted != 0,
        ConsoleId = row.ConsoleId,
        Title = row.Title,
        TitleJa = row.TitleJa ?? string.Empty,
        TitleKana = row.TitleKana ?? string.Empty,
        Publisher = row.Publisher,
        ReleaseYear = row.ReleaseYear,
        Players = row.Players,
        Genre = row.Genre,
        Description = row.Description,
        LastPlayed = row.LastPlayed is null
            ? null
            : DateTimeOffset.Parse(row.LastPlayed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        PlayTimeMinutes = row.PlayTimeMinutes,
        IconPath = row.IconPath,
        Custom = row.Custom ?? string.Empty,
    };

    private static object ToParameters(RomFile file) => new
    {
        file.No,
        file.ParentId,
        FileId = file.FileId.ToString(),
        VolumeSerial = file.VolumeSerial.ToString(CultureInfo.InvariantCulture),
        file.FileName,
        IsDeleted = file.IsDeleted ? 1 : 0,
        file.ConsoleId,
        file.Title,
        TitleJa = file.TitleJa ?? string.Empty,
        TitleKana = file.TitleKana ?? string.Empty,
        file.Publisher,
        file.ReleaseYear,
        file.Players,
        file.Genre,
        file.Description,
        LastPlayed = file.LastPlayed?.ToString("O", CultureInfo.InvariantCulture),
        file.PlayTimeMinutes,
        file.IconPath,
        Custom = file.Custom ?? string.Empty,
    };

    /// <summary>
    /// 一覧に出すゲームの条件(SQLの断片)。複数枚組(.m3u)のメンバーになっているディスクは、
    /// セット側(.m3u)が有効な間は一覧から隠す(メンバーの対応表はRomDiscMember)。
    /// 「RomFile」テーブルを別名なしで参照するクエリの、WHERE句の末尾へANDで足して使う。
    /// </summary>
    internal const string NotHiddenDiscMember =
        "NOT EXISTS (SELECT 1 FROM RomDiscMember d JOIN RomFile s ON s.No = d.SetNo "
        + "WHERE d.MemberNo = RomFile.No AND s.IsDeleted = 0)";

    // ======================================================================
    // CRUD
    // ======================================================================

    /// <summary>新規追加し、SQLite側でAUTOINCREMENT採番されたNoをfile.Noへ反映して返す
    /// (RomFolderRepository.InsertAsyncと同じ考え方)。</summary>
    public static async Task<int> InsertAsync(RomFile file)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            INSERT INTO RomFile
                (ParentId, FileId, VolumeSerial, FileName, IsDeleted, ConsoleId,
                 Title, TitleJa, TitleKana, Publisher, ReleaseYear, Players, Genre, Description,
                 LastPlayed, PlayTimeMinutes, IconPath, Custom)
            VALUES
                (@ParentId, @FileId, @VolumeSerial, @FileName, @IsDeleted, @ConsoleId,
                 @Title, @TitleJa, @TitleKana, @Publisher, @ReleaseYear, @Players, @Genre, @Description,
                 @LastPlayed, @PlayTimeMinutes, @IconPath, @Custom);
            SELECT last_insert_rowid();";

        long newNo = await connection.ExecuteScalarAsync<long>(sql, ToParameters(file)).ConfigureAwait(false);
        file.No = checked((int)newNo);
        return file.No;
    }

    /// <summary>Noを指定して1件取得する。論理削除済みでも取得できる(呼び出し側でIsDeletedを見て判断する)。</summary>
    public static async Task<RomFile?> GetAsync(int no)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "SELECT * FROM RomFile WHERE No = @No;";
        RomFileRow? row = await connection.QuerySingleOrDefaultAsync<RomFileRow>(sql, new { No = no })
            .ConfigureAwait(false);

        return row is null ? null : ToModel(row);
    }

    /// <summary>
    /// VolumeSerial+FileId(NTFS上でこのファイルを一意に識別する情報)から既存の登録を探す。
    /// 再スキャン時に「同じファイルかどうか」をパス文字列ではなくこちらで判定する想定。
    /// </summary>
    public static async Task<RomFile?> FindByVolumeAndFileIdAsync(ulong volumeSerial, Guid fileId)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "SELECT * FROM RomFile WHERE VolumeSerial = @VolumeSerial AND FileId = @FileId;";
        RomFileRow? row = await connection.QuerySingleOrDefaultAsync<RomFileRow>(sql, new
        {
            VolumeSerial = volumeSerial.ToString(CultureInfo.InvariantCulture),
            FileId = fileId.ToString(),
        }).ConfigureAwait(false);

        return row is null ? null : ToModel(row);
    }

    /// <summary>登録済みの全ファイルを取得する。既定では論理削除済み(IsDeleted=true)を除外する。</summary>
    public static async Task<IReadOnlyList<RomFile>> GetAllAsync(bool includeDeleted = false)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        string sql = includeDeleted
            ? "SELECT * FROM RomFile;"
            : "SELECT * FROM RomFile WHERE IsDeleted = 0;";

        IEnumerable<RomFileRow> rows = await connection.QueryAsync<RomFileRow>(sql).ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    /// <summary>指定した親フォルダ(RomFolder.No)配下のファイルだけを取得する。既定では論理削除済みを除外する。</summary>
    public static async Task<IReadOnlyList<RomFile>> GetByParentIdAsync(int parentFolderNo, bool includeDeleted = false)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        string sql = includeDeleted
            ? "SELECT * FROM RomFile WHERE ParentId = @ParentId;"
            : "SELECT * FROM RomFile WHERE ParentId = @ParentId AND IsDeleted = 0;";

        IEnumerable<RomFileRow> rows = await connection
            .QueryAsync<RomFileRow>(sql, new { ParentId = parentFolderNo })
            .ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    /// <summary>指定コンソールに紐づくファイルだけを取得する。既定では論理削除済みを除外する。</summary>
    public static async Task<IReadOnlyList<RomFile>> GetByConsoleIdAsync(string consoleId, bool includeDeleted = false)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        string sql = includeDeleted
            ? "SELECT * FROM RomFile WHERE ConsoleId = @ConsoleId;"
            : "SELECT * FROM RomFile WHERE ConsoleId = @ConsoleId AND IsDeleted = 0 AND " + NotHiddenDiscMember + ";";

        IEnumerable<RomFileRow> rows = await connection.QueryAsync<RomFileRow>(sql, new { ConsoleId = consoleId })
            .ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    /// <summary>最近遊んだゲーム(LastPlayedが新しい順)を、論理削除済みを除いて最大limit件取得する。</summary>
    public static async Task<IReadOnlyList<RomFile>> GetRecentlyPlayedAsync(int limit)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "SELECT * FROM RomFile WHERE IsDeleted = 0 AND LastPlayed IS NOT NULL AND " + NotHiddenDiscMember
            + " ORDER BY LastPlayed DESC LIMIT @Limit;";
        IEnumerable<RomFileRow> rows = await connection.QueryAsync<RomFileRow>(sql, new { Limit = limit })
            .ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    private sealed class ConsoleCountRow
    {

        public string ConsoleId { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>
    /// コンソールId(ConsoleInfo.Id)ごとの登録タイトル数(論理削除済みを除く)をまとめて取得する。
    /// ConsoleSelectPageが、コンソールカードの「xxx タイトル」表示を実データに合わせる
    /// (0件のコンソールはカードごと非表示にする)ために使う。1件も無いコンソールIdは
    /// 結果に含まれないため、呼び出し側は辞書に無いIdを0件として扱うこと。
    /// </summary>
    public static async Task<Dictionary<string, int>> GetCountsByConsoleIdAsync()
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            SELECT ConsoleId, COUNT(*) AS Count
            FROM RomFile
            WHERE IsDeleted = 0 AND " + NotHiddenDiscMember + @"
            GROUP BY ConsoleId;";

        IEnumerable<ConsoleCountRow> rows = await connection.QueryAsync<ConsoleCountRow>(sql).ConfigureAwait(false);
        return rows.ToDictionary(row => row.ConsoleId, row => row.Count);
    }

    /// <summary>
    /// 指定したNoの集合に一致するファイルをまとめて取得する(コレクション機能で、
    /// RomCollectionInfo.GameIdsに列挙されたRomFile.Noから実際のRomFileを解決するのに使う)。
    /// 既定では論理削除済みを除外する。結果の順序はnos引数の並び順に揃える
    /// (SQLiteのIN句は順序を保証しないため)。noに一致する行が無いものは単に無視する。
    /// </summary>
    public static async Task<IReadOnlyList<RomFile>> GetByNosAsync(IReadOnlyCollection<int> nos, bool includeDeleted = false)
    {
        if (nos.Count == 0)
        {
            return Array.Empty<RomFile>();
        }

        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        string sql = includeDeleted
            ? "SELECT * FROM RomFile WHERE No IN @Nos;"
            : "SELECT * FROM RomFile WHERE No IN @Nos AND IsDeleted = 0 AND " + NotHiddenDiscMember + ";";

        IEnumerable<RomFileRow> rows = await connection.QueryAsync<RomFileRow>(sql, new { Nos = nos })
            .ConfigureAwait(false);

        Dictionary<int, RomFile> byNo = rows.Select(ToModel).ToDictionary(f => f.No);
        return nos
            .Select(no => byNo.TryGetValue(no, out RomFile? file) ? file : null)
            .Where(file => file is not null)
            .Select(file => file!)
            .ToList();
    }

    /// <summary>
    /// 既存レコードの基本情報を更新する(Noで特定)。該当するNoが無ければfalseを返す。
    /// ゲーム別の個別設定(Custom。ランチャーもここに入る)は、画面が持つ古い値で他の項目を消してしまわないよう
    /// ここでは更新しない(<see cref="UpdateCustomAsync"/> を使う)。
    /// </summary>
    public static async Task<bool> UpdateAsync(RomFile file)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            UPDATE RomFile SET
                ParentId = @ParentId,
                FileId = @FileId,
                VolumeSerial = @VolumeSerial,
                FileName = @FileName,
                IsDeleted = @IsDeleted,
                ConsoleId = @ConsoleId,
                Title = @Title,
                TitleJa = @TitleJa,
                TitleKana = @TitleKana,
                Publisher = @Publisher,
                ReleaseYear = @ReleaseYear,
                Players = @Players,
                Genre = @Genre,
                Description = @Description,
                LastPlayed = @LastPlayed,
                PlayTimeMinutes = @PlayTimeMinutes,
                IconPath = @IconPath
            WHERE No = @No;";

        int affected = await connection.ExecuteAsync(sql, ToParameters(file)).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// 「ゲーム情報の編集」の保存。タイトル・日本語タイトル・読み・発売元・発売年・プレイ人数・ジャンル・概要だけを更新する
    /// (プレイ時間・最後に遊んだ日時・個別設定などは触らない)。
    /// <paramref name="iconFileName"/> がnullなら画像(IconPath)は変えない。値はファイル名だけ
    /// (&lt;親フォルダ&gt;\RetroNexus\Named_Boxarts の中のファイル名。画面が持つフルパスを渡さないこと)。
    /// 該当するNoが無ければfalseを返す。
    /// </summary>
    public static async Task<bool> UpdateGameInfoAsync(
        int no, string title, string titleJa, string titleKana, string publisher, int releaseYear,
        string players, string genre, string description, string? iconFileName)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        string sql = @"
            UPDATE RomFile SET
                Title = @Title,
                TitleJa = @TitleJa,
                TitleKana = @TitleKana,
                Publisher = @Publisher,
                ReleaseYear = @ReleaseYear,
                Players = @Players,
                Genre = @Genre,
                Description = @Description"
            + (iconFileName is null ? string.Empty : ",\n                IconPath = @IconPath")
            + @"
            WHERE No = @No;";

        int affected = await connection.ExecuteAsync(sql, new
        {
            No = no, Title = title, TitleJa = titleJa, TitleKana = titleKana, Publisher = publisher,
            ReleaseYear = releaseYear, Players = players, Genre = genre, Description = description,
            IconPath = iconFileName ?? string.Empty,
        }).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>このゲームだけで使うランチャー名を保存する(空文字=コンソールの既定ランチャー)。
    /// 該当するNoが無ければfalseを返す。</summary>
    public static async Task<bool> UpdateLauncherAsync(int no, string launcher)
    {
        string? saved = await UpdateCustomAsync(no, custom => RomCustom.SetLauncher(custom, launcher ?? string.Empty))
            .ConfigureAwait(false);
        return saved is not null;
    }

    private sealed class CustomRow
    {
        public string? Custom { get; set; }
    }

    /// <summary>
    /// このゲームの個別設定(Custom)を部分更新する。DBの現在の値を読み、<paramref name="mutate"/> で
    /// 該当する項目だけ書き換えて保存するので、ほかの項目(ランチャー・コントローラ設定など)は残る。
    /// 保存後のCustom(JSON文字列。空なら空文字)を返す。該当するNoが無ければnull。
    /// </summary>
    public static async Task<string?> UpdateCustomAsync(int no, Action<JsonObject> mutate)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        CustomRow? row = await connection.QuerySingleOrDefaultAsync<CustomRow>(
            "SELECT Custom FROM RomFile WHERE No = @No;", new { No = no }, transaction)
            .ConfigureAwait(false);
        if (row is null) return null;

        JsonObject custom = RomCustom.Parse(row.Custom);
        mutate(custom);

        string serialized = RomCustom.Serialize(custom);
        await connection.ExecuteAsync(
            "UPDATE RomFile SET Custom = @Custom WHERE No = @No;",
            new { Custom = serialized, No = no }, transaction).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);

        return serialized;
    }

    /// <summary>
    /// ゲームを遊び終えた結果を記録する。LastPlayedを上書きし、PlayTimeMinutesへaddMinutesを加算する
    /// (読み出してから書き戻さず、1回のUPDATEで加算するので、同時に別の更新が入っても加算が失われない)。
    /// 該当するNoが無ければfalseを返す。
    /// </summary>
    public static async Task<bool> RecordPlayAsync(int no, DateTimeOffset lastPlayed, int addMinutes)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            UPDATE RomFile SET
                LastPlayed = @LastPlayed,
                PlayTimeMinutes = PlayTimeMinutes + @AddMinutes
            WHERE No = @No;";

        int affected = await connection.ExecuteAsync(sql, new
        {
            No = no,
            LastPlayed = lastPlayed.ToString("O", CultureInfo.InvariantCulture),
            AddMinutes = Math.Max(0, addMinutes),
        }).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// 論理削除(IsDeleted=1)する。物理的な行は残るため、後から<see cref="RestoreAsync"/>で戻せる。
    /// 該当するNoが無ければfalseを返す。
    /// </summary>
    public static async Task<bool> SoftDeleteAsync(int no)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "UPDATE RomFile SET IsDeleted = 1 WHERE No = @No;";
        int affected = await connection.ExecuteAsync(sql, new { No = no }).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>論理削除(IsDeleted=1)を取り消す(IsDeleted=0に戻す)。該当するNoが無ければfalseを返す。</summary>
    public static async Task<bool> RestoreAsync(int no)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "UPDATE RomFile SET IsDeleted = 0 WHERE No = @No;";
        int affected = await connection.ExecuteAsync(sql, new { No = no }).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>行そのものを物理削除する(元に戻せない)。論理削除で足りる場合は<see cref="SoftDeleteAsync"/>を使うこと。</summary>
    public static async Task<bool> DeleteAsync(int no)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "DELETE FROM RomFile WHERE No = @No;";
        int affected = await connection.ExecuteAsync(sql, new { No = no }).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// file.ParentId(RomFolder.Noそのもの)から親フォルダをRomFolderRepository経由で引き、
    /// RomFolder.PathとFileNameを結合したフルパスを返す。該当するRomFolderが見つからない
    /// (削除済みも含む)場合はnullを返す。
    /// </summary>
    public static async Task<string?> ResolveFullPathAsync(RomFile file)
    {
        RomFolder? folder = await RomFolderRepository.GetAsync(file.ParentId).ConfigureAwait(false);
        if (folder is null)
        {
            return null;
        }

        // FileNameはサブフォルダ内のゲームでは「サブフォルダ名/ファイル名」の相対パスなので、区切りをOSに合わせる。
        return Path.Combine(folder.Path, file.FileName.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// 一覧分のRomFile.IconPath(スキャン時に保存された、ファイル名だけの値。CRC-32ベース。
    /// 例: "017FA7E9.png")を、実際に画面表示へ使えるフルパス
    /// ("&lt;親RomFolder.Path&gt;\RetroNexus\Named_Boxarts\&lt;ファイル名&gt;")へその場で
    /// 書き換える(DBへは書き戻さない。あくまで表示用の変換)。
    /// 同じ親フォルダ(ParentId)を持つ複数のRomFileをまとめて渡した場合、RomFolderの
    /// 問い合わせはParentIdごとに1回だけで済むようキャッシュする。
    /// IconPathが空、または親フォルダが見つからない場合はそのRomFileのIconPathを
    /// 空文字にする(呼び出し側の「IconPathが空ならフォールバック表示」という
    /// 既存の仕組みがそのまま機能する)。
    /// </summary>
    public static async Task ResolveIconPathsAsync(IEnumerable<RomFile> files)
    {
        var folderCache = new Dictionary<int, RomFolder?>();

        foreach (RomFile file in files)
        {
            if (string.IsNullOrEmpty(file.IconPath))
            {
                continue;
            }

            if (!folderCache.TryGetValue(file.ParentId, out RomFolder? folder))
            {
                folder = await RomFolderRepository.GetAsync(file.ParentId).ConfigureAwait(false);
                folderCache[file.ParentId] = folder;
            }

            file.IconPath = folder is null
                ? string.Empty
                : Path.Combine(folder.Path, "RetroNexus", "Named_Boxarts", file.IconPath);
        }
    }
}
