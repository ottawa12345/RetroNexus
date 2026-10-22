using System.Globalization;
using Dapper;
using EmulatorLauncher.Models;
using Microsoft.Data.Sqlite;

namespace EmulatorLauncher.Storage;

/// <summary>RomFolderテーブル(NTFSのVolumeSerial+FolderIdで一意に識別されるフォルダのスキャン管理情報)への非同期CRUDアクセス。</summary>
public static class RomFolderRepository
{
    // Dapper用の行マッピングDTO。
    private sealed class RomFolderRow
    {
        public int No { get; set; }
        public string FolderId { get; set; } = string.Empty;
        public string VolumeSerial { get; set; } = string.Empty;
        public string LastModifyTimeAt { get; set; } = string.Empty;
        public string LastScannedAt { get; set; } = string.Empty;
        public string ConsoleId { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long IsDeleted { get; set; }
    }

    private static RomFolder ToModel(RomFolderRow row) => new()
    {
        No = row.No,
        FolderId = Guid.Parse(row.FolderId),
        VolumeSerial = ulong.Parse(row.VolumeSerial, CultureInfo.InvariantCulture),
        LastModifyTimeAt = DateTime.Parse(row.LastModifyTimeAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        LastScannedAt = DateTime.Parse(row.LastScannedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ConsoleId = row.ConsoleId,
        Path = row.Path,
        IsDeleted = row.IsDeleted != 0,
    };

    private static object ToParameters(RomFolder folder) => new
    {
        folder.No,
        FolderId = folder.FolderId.ToString(),
        VolumeSerial = folder.VolumeSerial.ToString(CultureInfo.InvariantCulture),
        LastModifyTimeAt = folder.LastModifyTimeAt.ToString("O", CultureInfo.InvariantCulture),
        LastScannedAt = folder.LastScannedAt.ToString("O", CultureInfo.InvariantCulture),
        folder.ConsoleId,
        folder.Path,
        IsDeleted = folder.IsDeleted ? 1 : 0,
    };

    // CRUD

    /// <summary>新規追加し、SQLite側でAUTOINCREMENT採番されたNoをfolder.Noへ反映して返す。</summary>
    public static async Task<int> InsertAsync(RomFolder folder)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            INSERT INTO RomFolder
                (FolderId, VolumeSerial, LastModifyTimeAt, LastScannedAt, ConsoleId, Path, IsDeleted)
            VALUES
                (@FolderId, @VolumeSerial, @LastModifyTimeAt, @LastScannedAt, @ConsoleId, @Path, @IsDeleted);
            SELECT last_insert_rowid();";

        long newNo = await connection.ExecuteScalarAsync<long>(sql, ToParameters(folder)).ConfigureAwait(false);
        folder.No = checked((int)newNo);
        return folder.No;
    }

    /// <summary>Noを指定して1件取得する。</summary>
    public static async Task<RomFolder?> GetAsync(int no)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "SELECT * FROM RomFolder WHERE No = @No;";
        RomFolderRow? row = await connection.QuerySingleOrDefaultAsync<RomFolderRow>(sql, new { No = no })
            .ConfigureAwait(false);

        return row is null ? null : ToModel(row);
    }

    /// <summary>VolumeSerial+FolderIdの組み合わせ(NTFS上でそのフォルダを一意に識別する情報)から既存の登録を探す。</summary>
    public static async Task<RomFolder?> FindByVolumeAndFolderIdAsync(ulong volumeSerial, Guid folderId)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "SELECT * FROM RomFolder WHERE VolumeSerial = @VolumeSerial AND FolderId = @FolderId;";
        RomFolderRow? row = await connection.QuerySingleOrDefaultAsync<RomFolderRow>(sql, new
        {
            VolumeSerial = volumeSerial.ToString(CultureInfo.InvariantCulture),
            FolderId = folderId.ToString(),
        }).ConfigureAwait(false);

        return row is null ? null : ToModel(row);
    }

    /// <summary>登録済みの全フォルダを取得する。</summary>
    public static async Task<IReadOnlyList<RomFolder>> GetAllAsync(bool includeDeleted = false)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        string sql = includeDeleted
            ? "SELECT * FROM RomFolder ORDER BY No;"
            : "SELECT * FROM RomFolder WHERE IsDeleted = 0 ORDER BY No;";

        IEnumerable<RomFolderRow> rows = await connection.QueryAsync<RomFolderRow>(sql).ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    /// <summary>指定コンソールに紐づくフォルダだけを取得する。</summary>
    public static async Task<IReadOnlyList<RomFolder>> GetByConsoleIdAsync(string consoleId, bool includeDeleted = false)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        string sql = includeDeleted
            ? "SELECT * FROM RomFolder WHERE ConsoleId = @ConsoleId ORDER BY No;"
            : "SELECT * FROM RomFolder WHERE ConsoleId = @ConsoleId AND IsDeleted = 0 ORDER BY No;";

        IEnumerable<RomFolderRow> rows = await connection.QueryAsync<RomFolderRow>(sql, new { ConsoleId = consoleId })
            .ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    /// <summary>既存レコードを更新する(Noで特定)。</summary>
    public static async Task<bool> UpdateAsync(RomFolder folder)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = @"
            UPDATE RomFolder SET
                FolderId = @FolderId,
                VolumeSerial = @VolumeSerial,
                LastModifyTimeAt = @LastModifyTimeAt,
                LastScannedAt = @LastScannedAt,
                ConsoleId = @ConsoleId,
                Path = @Path,
                IsDeleted = @IsDeleted
            WHERE No = @No;";

        int affected = await connection.ExecuteAsync(sql, ToParameters(folder)).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>論理削除(IsDeleted=1)する。</summary>
    public static async Task<bool> SoftDeleteAsync(int no)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "UPDATE RomFolder SET IsDeleted = 1 WHERE No = @No;";
        int affected = await connection.ExecuteAsync(sql, new { No = no }).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>論理削除(IsDeleted=1)を取り消す(IsDeleted=0に戻す)。</summary>
    public static async Task<bool> RestoreAsync(int no)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "UPDATE RomFolder SET IsDeleted = 0 WHERE No = @No;";
        int affected = await connection.ExecuteAsync(sql, new { No = no }).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>行そのものを物理削除する(元に戻せない)。</summary>
    public static async Task<bool> DeleteAsync(int no)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        const string sql = "DELETE FROM RomFolder WHERE No = @No;";
        int affected = await connection.ExecuteAsync(sql, new { No = no }).ConfigureAwait(false);
        return affected > 0;
    }
}
