using Dapper;
using Microsoft.Data.Sqlite;

namespace EmulatorLauncher.Storage;

/// <summary>
/// RomDiscMember(複数枚組のゲーム=.m3uのRomFileと、そのメンバーのディスクの対応表)への読み書き。
/// この表は.m3uファイルの中身から作る索引で、正はあくまで.m3uファイル。
/// 呼び出し側は主にEmulatorLauncher.RomLibrary.RomDiscSetService(スキャンの後処理と、画面からのまとめ/解除)。
/// </summary>
public static class RomDiscSetRepository
{
    /// <summary>セットのメンバー(RomFile.No)を、並び順(Position)のとおりに返す。</summary>
    public static async Task<IReadOnlyList<int>> GetMemberNosAsync(int setNo)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        IEnumerable<int> rows = await connection.QueryAsync<int>(
            "SELECT MemberNo FROM RomDiscMember WHERE SetNo = @SetNo ORDER BY Position;",
            new { SetNo = setNo }).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>
    /// 有効なセット(論理削除されていない.m3u)のメンバーになっているRomFile.Noの集合。
    /// 「まとめる」の候補から、すでに別のセットに入っているディスクを外すのに使う。
    /// </summary>
    public static async Task<HashSet<int>> GetActiveMemberNosAsync()
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        IEnumerable<int> rows = await connection.QueryAsync<int>(
            @"SELECT d.MemberNo FROM RomDiscMember d
              JOIN RomFile s ON s.No = d.SetNo
              WHERE s.IsDeleted = 0;").ConfigureAwait(false);
        return new HashSet<int>(rows);
    }

    /// <summary>
    /// セットのメンバーを、渡した順(0始まりのPosition)に置き換える。いまの内容と同じなら何も書かない。
    /// 書き込んだ(内容が変わった)ときtrue。
    /// </summary>
    public static async Task<bool> ReplaceMembersAsync(int setNo, IReadOnlyList<int> memberNos)
    {
        IReadOnlyList<int> current = await GetMemberNosAsync(setNo).ConfigureAwait(false);
        if (current.SequenceEqual(memberNos)) return false;

        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync().ConfigureAwait(false);

        await connection.ExecuteAsync("DELETE FROM RomDiscMember WHERE SetNo = @SetNo;",
            new { SetNo = setNo }, transaction).ConfigureAwait(false);
        for (int i = 0; i < memberNos.Count; i++)
        {
            await connection.ExecuteAsync(
                "INSERT INTO RomDiscMember (SetNo, Position, MemberNo) VALUES (@SetNo, @Position, @MemberNo);",
                new { SetNo = setNo, Position = i, MemberNo = memberNos[i] }, transaction).ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>セットのメンバー行をすべて消す(.m3uのまとめを解除したとき)。</summary>
    public static async Task DeleteSetAsync(int setNo)
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        await connection.ExecuteAsync("DELETE FROM RomDiscMember WHERE SetNo = @SetNo;",
            new { SetNo = setNo }).ConfigureAwait(false);
    }

    /// <summary>
    /// RomFileに存在しないセット、または存在しないメンバーを指している行を消す
    /// (.m3uやディスクが削除された後の掃除)。
    /// </summary>
    public static async Task PurgeOrphansAsync()
    {
        await using SqliteConnection connection = await DatabaseManager.OpenConnectionAsync().ConfigureAwait(false);

        await connection.ExecuteAsync(
            @"DELETE FROM RomDiscMember
              WHERE SetNo NOT IN (SELECT No FROM RomFile)
                 OR MemberNo NOT IN (SELECT No FROM RomFile);").ConfigureAwait(false);
    }
}
