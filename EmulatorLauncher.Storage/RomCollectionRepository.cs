using System.Globalization;
using Dapper;
using EmulatorLauncher.Models;
using Microsoft.Data.Sqlite;

namespace EmulatorLauncher.Storage;

/// <summary>
/// RomCollectionテーブル(ユーザー定義のゲームまとめ。「お気に入り」等)と、その所属ゲームを
/// 表すRomCollectionItemテーブル(RomCollection⇔RomFileの多対多の橋渡し)への非同期CRUD
/// アクセス。設計方針・接続の扱い方はRomFolderRepository/RomFileRepositoryと同じで、
/// DBファイルの場所・接続文字列・PRAGMA設定・スキーマ作成は<see cref="DatabaseManager"/>が
/// 一元管理しており、このクラスは<see cref="DatabaseManager.OpenConnectionAsync"/>を通じて
/// 得た接続でSQLを実行するだけ(SqliteConnectionを自前でnewしない)。
/// </summary>
public static class RomCollectionRepository
{
    /// <summary>
    /// 「お気に入り」コレクションの固定Id。DatabaseManagerの初期化時に無ければ自動作成される
    /// ため、常に存在する前提で参照してよい。
    /// </summary>
    public const string FavoriteCollectionId = "favorite";

    // ======================================================================
    // Dapper用の行マッピングDTO(RomFolderRepositoryと同じ考え方)。
    // ======================================================================
    private sealed class RomCollectionRow
    {
        public int No { get; set; }
        public string CollectionId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public string AccentColorHex { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public long IsDeleted { get; set; }
    }

    private sealed class CollectionItemRow
    {
        public int CollectionNo { get; set; }
        public int RomFileNo { get; set; }
    }

    private static RomCollectionInfo ToModel(RomCollectionRow row, List<int> gameIds) => new()
    {
        Id = row.CollectionId,
        Name = row.Name,
        IconPath = row.IconPath,
        AccentColorHex = row.AccentColorHex,
        GameIds = gameIds,
    };

    // ======================================================================
    // 参照
    // ======================================================================

    /// <summary>
    /// 全コレクションを、それぞれの所属RomFile.No一覧(GameIds)付きで取得する。
    /// 論理削除済みのコレクション、および論理削除済みのRomFileへの所属は除外する。
    /// SortOrder順(「お気に入り」は-1固定なので常に先頭)、同順位ならNo順に並べる。
    /// </summary>
    public static async Task<List<RomCollectionInfo>> GetAllAsync()
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string collectionsSql = "SELECT * FROM RomCollection WHERE IsDeleted = 0 ORDER BY SortOrder, No;";
        IEnumerable<RomCollectionRow> collectionRows = await connection
            .QueryAsync<RomCollectionRow>(collectionsSql).ConfigureAwait(false);
        List<RomCollectionRow> collections = collectionRows.ToList();

        const string itemsSql = @"
            SELECT rci.CollectionNo AS CollectionNo, rci.RomFileNo AS RomFileNo
            FROM RomCollectionItem rci
            INNER JOIN RomFile rf ON rf.No = rci.RomFileNo
            WHERE rf.IsDeleted = 0;";
        IEnumerable<CollectionItemRow> itemRows = await connection
            .QueryAsync<CollectionItemRow>(itemsSql).ConfigureAwait(false);

        Dictionary<int, List<int>> gameIdsByCollectionNo = itemRows
            .GroupBy(item => item.CollectionNo)
            .ToDictionary(group => group.Key, group => group.Select(item => item.RomFileNo).ToList());

        return collections
            .Select(row => ToModel(
                row,
                gameIdsByCollectionNo.TryGetValue(row.No, out List<int>? gameIds) ? gameIds : new List<int>()))
            .ToList();
    }

    /// <summary>
    /// 指定したRomFile(No)が現在所属している全コレクションのCollectionIdを取得する
    /// (ゲーム選択画面の詳細画面が、各コレクションのトグルボタンの初期状態を決めるのに使う)。
    /// </summary>
    public static async Task<List<string>> GetCollectionIdsForRomFileAsync(int romFileNo)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            SELECT rc.CollectionId
            FROM RomCollectionItem rci
            INNER JOIN RomCollection rc ON rc.No = rci.CollectionNo
            WHERE rci.RomFileNo = @RomFileNo AND rc.IsDeleted = 0;";

        IEnumerable<string> ids = await connection
            .QueryAsync<string>(sql, new { RomFileNo = romFileNo }).ConfigureAwait(false);
        return ids.ToList();
    }



    // ======================================================================
    // 所属の追加/解除
    // ======================================================================

    /// <summary>
    /// 指定コレクションへ指定RomFileを追加する(既に入っていれば何もしない)。
    /// collectionIdに一致するコレクションが無い(削除済み含む)場合はfalseを返す。
    /// </summary>
    public static async Task<bool> AddToCollectionAsync(string collectionId, int romFileNo)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        int? collectionNo = await connection.ExecuteScalarAsync<int?>(
            "SELECT No FROM RomCollection WHERE CollectionId = @CollectionId AND IsDeleted = 0;",
            new { CollectionId = collectionId }).ConfigureAwait(false);

        if (collectionNo is null)
        {
            return false;
        }

        const string sql = @"
            INSERT OR IGNORE INTO RomCollectionItem (CollectionNo, RomFileNo, AddedAt)
            VALUES (@CollectionNo, @RomFileNo, @AddedAt);";

        await connection.ExecuteAsync(sql, new
        {
            CollectionNo = collectionNo.Value,
            RomFileNo = romFileNo,
            AddedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        }).ConfigureAwait(false);

        return true;
    }

    /// <summary>指定コレクションから指定RomFileを外す(元々入っていなければ何もしない)。</summary>
    public static async Task<bool> RemoveFromCollectionAsync(string collectionId, int romFileNo)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            DELETE FROM RomCollectionItem
            WHERE RomFileNo = @RomFileNo
              AND CollectionNo = (SELECT No FROM RomCollection WHERE CollectionId = @CollectionId);";

        int affected = await connection
            .ExecuteAsync(sql, new { CollectionId = collectionId, RomFileNo = romFileNo }).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// 指定したRomFile群を、所属している全コレクション(お気に入り含む)から一括で外す。
    /// ROMフォルダの登録解除(Services.RomFolderScanService.RemoveFolderAsync)で、
    /// RomFile自体をデータベースから削除する前に、RomCollectionItemのFOREIGN KEY制約を
    /// 満たすために必ず先に呼ぶ必要がある。romFileNosが空なら何もしない。
    /// </summary>
    public static async Task RemoveRomFilesFromAllCollectionsAsync(IReadOnlyCollection<int> romFileNos)
    {
        if (romFileNos.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "DELETE FROM RomCollectionItem WHERE RomFileNo IN @RomFileNos;";
        await connection.ExecuteAsync(sql, new { RomFileNos = romFileNos }).ConfigureAwait(false);
    }

    // ======================================================================
    // 作成/更新(コレクション新規追加・編集画面から呼ばれる)
    // ======================================================================

    /// <summary>
    /// 新しいコレクションを作成する(コレクション一覧末尾の「新規追加」カードから呼ばれる)。
    /// CollectionIdは表示名Nameとは独立した内部参照キーとしてGuidから生成する。
    /// アイコン未選択の場合、iconPathは空文字のまま渡してよい。
    /// </summary>
    public static async Task CreateAsync(string name, string iconPath)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        int nextSortOrder = await connection.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM RomCollection WHERE IsDeleted = 0;").ConfigureAwait(false);

        const string sql = @"
            INSERT INTO RomCollection (CollectionId, Name, IconPath, AccentColorHex, SortOrder)
            VALUES (@CollectionId, @Name, @IconPath, @AccentColorHex, @SortOrder);";

        await connection.ExecuteAsync(sql, new
        {
            CollectionId = Guid.NewGuid().ToString("N"),
            Name = name,
            IconPath = iconPath ?? string.Empty,
            AccentColorHex = "#607D8B",
            SortOrder = nextSortOrder,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// コレクションの並び順を、指定したCollectionId順(先頭から1,2,3...)に保存する(設定画面の「コレクション並び替え」用)。
    /// 「お気に入り」はSortOrder=-1固定で常に先頭なので、変更しない。
    /// </summary>
    public static async Task ReorderAsync(IReadOnlyList<string> orderedIds)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

        int order = 1;
        foreach (string id in orderedIds)
        {
            if (id == FavoriteCollectionId) continue;
            await connection.ExecuteAsync(
                "UPDATE RomCollection SET SortOrder = @SortOrder WHERE CollectionId = @CollectionId;",
                new { SortOrder = order++, CollectionId = id }, transaction).ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 既存コレクションの名前/アイコンを更新する。CollectionEditPageを編集にも使い回せる
    /// ようにするための実装(編集を開く入り口のUIは今回のスコープには含めていない)。
    /// </summary>
    public static async Task UpdateAsync(string collectionId, string name, string iconPath)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            UPDATE RomCollection SET Name = @Name, IconPath = @IconPath
            WHERE CollectionId = @CollectionId;";

        await connection.ExecuteAsync(sql, new
        {
            CollectionId = collectionId,
            Name = name,
            IconPath = iconPath ?? string.Empty,
        }).ConfigureAwait(false);
    }

    /// <summary>コレクションを論理削除する。「お気に入り」は削除できない(常にfalse)。</summary>
    public static async Task<bool> SoftDeleteAsync(string collectionId)
    {
        if (collectionId == FavoriteCollectionId) return false;

        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "UPDATE RomCollection SET IsDeleted = 1 WHERE CollectionId = @CollectionId;";
        int affected = await connection.ExecuteAsync(sql, new { CollectionId = collectionId }).ConfigureAwait(false);
        return affected > 0;
    }
}
