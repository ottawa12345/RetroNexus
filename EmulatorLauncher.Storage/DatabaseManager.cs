using System.Threading;
using Dapper;
using Microsoft.Data.Sqlite;

namespace EmulatorLauncher.Storage;

/// <summary>
/// アプリ内のSQLiteデータベース(<c>&lt;実行ファイル&gt;/data/EmulatorLauncher.db</c>)への
/// アクセスを一元管理する窓口。
///
/// RomFolderRepository・RomFileRepository・RomCollectionRepositoryなど各テーブルの
/// リポジトリは、自前で
/// <see cref="SqliteConnection"/>を開かず、必ず<see cref="OpenConnectionAsync"/>経由で
/// 接続を取得する。これにより:
///   - DBファイルパス・接続文字列・PRAGMA設定(WAL/busy_timeout/foreign_keys)が
///     1箇所にまとまり、テーブルが増えても設定がバラつかない。
///   - スキーマ作成(CREATE TABLE等)を1つの初期化ロックで一括して行うため、
///     複数のリポジトリがそれぞれ独自に初期化を試みて競合する事態を避けられる
///     (以前はRomFolderRepository/RomFileRepositoryがそれぞれ別々の初期化ロックと
///     CREATE TABLE文を持っていたが、それらをすべてここへ統合した)。
///
/// ゲーム実行中はEmulationHostが専用スレッドでSDLの映像/音声ループを回している
/// 可能性があるため、ここで提供するメソッドはすべて非同期(Task)にし、どのスレッド
/// から呼んでも他の処理をブロックしないようにしている。<see cref="SqliteConnection"/>は
/// スレッドセーフではなく使い回しに向かないため、呼び出しのたびに新しい接続を
/// 開いて1回使い切る設計にしている。
/// </summary>
public static class DatabaseManager
{
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static bool _initialized;

    /// <summary>DBファイルの場所。設定ファイル(config フォルダ)と同じく、実行ファイル基準。</summary>
    private static string GetDatabasePath()
        => Path.Combine(AppContext.BaseDirectory, "data", "EmulatorLauncher.db");

    private static string BuildConnectionString(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        };
        return builder.ToString();
    }

    /// <summary>
    /// このDBへの新しい接続を開いて返す。未初期化ならスキーマ作成も済ませる。
    /// 呼び出し側は必ず <c>await using</c> で確実に破棄すること。
    /// これがアプリ内でSqliteConnectionを直接newしてよい唯一の場所であり、
    /// 各リポジトリはこのメソッドを介してのみDBへアクセスする。
    /// </summary>
    public static async Task<SqliteConnection> OpenConnectionAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync().ConfigureAwait(false);

        // 接続ごとに設定が必要なPRAGMA(journal_modeはDBファイル単位で永続化されるため
        // EnsureInitializedAsync側で一度設定すれば十分だが、busy_timeoutとforeign_keysは
        // 接続ごとの設定なので毎回かけ直す)。
        await connection.ExecuteAsync("PRAGMA busy_timeout=5000;").ConfigureAwait(false);
        await connection.ExecuteAsync("PRAGMA foreign_keys=ON;").ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    /// 使用中のままでも整合の取れたDBの複製を、指定のパスへ作る(バックアップ用。SQLiteのオンラインバックアップ)。
    /// </summary>
    public static async Task BackupToAsync(string destinationPath)
    {
        await using SqliteConnection source = await OpenConnectionAsync().ConfigureAwait(false);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,   // 閉じたらすぐファイルを手放す(続けて読み出す/削除するため)
        };
        await using var destination = new SqliteConnection(builder.ToString());
        await destination.OpenAsync().ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    /// <summary>
    /// dataフォルダ・DBファイル・全テーブル/インデックスが無ければ作成する。
    /// プロセス内で実際に走るのは最初の1回だけでよいので、SemaphoreSlim+フラグにより、
    /// どのスレッド・どのリポジトリから何度呼ばれても安全なようにしている
    /// (二重作成やレース状態を防ぐ)。
    /// </summary>
    private static async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await InitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            string dbPath = GetDatabasePath();
            string? folder = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using var connection = new SqliteConnection(BuildConnectionString(dbPath));
            await connection.OpenAsync().ConfigureAwait(false);

            await connection.ExecuteAsync("PRAGMA journal_mode=WAL;").ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA busy_timeout=5000;").ConfigureAwait(false);
            // ParentIdがRomFolder.Noを参照するFOREIGN KEY制約を実際に検証させるため有効化する
            // (SQLiteは既定でFOREIGN KEY制約チェックが無効なので、接続ごとに明示する必要がある)。
            await connection.ExecuteAsync("PRAGMA foreign_keys=ON;").ConfigureAwait(false);

            await CreateRomFolderTableAsync(connection).ConfigureAwait(false);
            await CreateRomFileTableAsync(connection).ConfigureAwait(false);
            await MigrateRomFileCustomAsync(connection).ConfigureAwait(false);
            await CreateRomCollectionTableAsync(connection).ConfigureAwait(false);
            await CreateRomCollectionItemTableAsync(connection).ConfigureAwait(false);
            await CreateRomDiscMemberTableAsync(connection).ConfigureAwait(false);
            await SeedBuiltinCollectionsAsync(connection).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            InitLock.Release();
        }
    }

    // ======================================================================
    // スキーマ定義。
    // 新しいテーブルを追加する際は、ここに Create*TableAsync を1つ足し、
    // EnsureInitializedAsync 内で呼び出しを追加するだけでよい
    // (テーブル定義をこの1ファイルに集約することで、全体のスキーマを見渡しやすくする)。
    // ======================================================================

    private static async Task CreateRomFolderTableAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            @"CREATE TABLE IF NOT EXISTS RomFolder (
                No INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderId TEXT NOT NULL,
                VolumeSerial TEXT NOT NULL,
                LastModifyTimeAt TEXT NOT NULL,
                LastScannedAt TEXT NOT NULL,
                ConsoleId TEXT NOT NULL,
                Path TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0
            );").ConfigureAwait(false);

        // VolumeSerial+FolderIdの組み合わせがNTFS上の一意識別情報なので、DBとしても一意制約を掛けておく。
        await connection.ExecuteAsync(
            @"CREATE UNIQUE INDEX IF NOT EXISTS IX_RomFolder_Volume_Folder
                ON RomFolder (VolumeSerial, FolderId);").ConfigureAwait(false);
    }

    private static async Task CreateRomFileTableAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            @"CREATE TABLE IF NOT EXISTS RomFile (
                No INTEGER PRIMARY KEY AUTOINCREMENT,
                ParentId INTEGER NOT NULL,
                FileId TEXT NOT NULL,
                VolumeSerial TEXT NOT NULL,
                FileName TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                ConsoleId TEXT NOT NULL,
                Title TEXT NOT NULL,
                TitleJa TEXT NOT NULL DEFAULT '',
                TitleKana TEXT NOT NULL DEFAULT '',
                Publisher TEXT NOT NULL,
                ReleaseYear INTEGER NOT NULL,
                Players TEXT NOT NULL,
                Genre TEXT NOT NULL,
                Description TEXT NOT NULL,
                LastPlayed TEXT NULL,
                PlayTimeMinutes INTEGER NOT NULL,
                IconPath TEXT NOT NULL,
                Custom TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ParentId) REFERENCES RomFolder (No)
            );").ConfigureAwait(false);


        // VolumeSerial+FileIdの組み合わせがNTFS上の一意識別情報なので、DBとしても一意制約を掛けておく。
        await connection.ExecuteAsync(
            @"CREATE UNIQUE INDEX IF NOT EXISTS IX_RomFile_Volume_File
                ON RomFile (VolumeSerial, FileId);").ConfigureAwait(false);

        // 親フォルダ単位・コンソール単位での一覧取得を高速化するための索引。
        await connection.ExecuteAsync(
            @"CREATE INDEX IF NOT EXISTS IX_RomFile_ParentId ON RomFile (ParentId);").ConfigureAwait(false);
        await connection.ExecuteAsync(
            @"CREATE INDEX IF NOT EXISTS IX_RomFile_ConsoleId ON RomFile (ConsoleId);").ConfigureAwait(false);
    }

    private sealed class LauncherMigrationRow
    {
        public long No { get; set; }
        public string? Launcher { get; set; }
        public string? Custom { get; set; }
    }

    /// <summary>
    /// RomFile.Custom列(ゲーム個別設定のJSON)が無い既存のDBへ列を足し、従来のLauncher列の値を
    /// Custom内の"launcher"へ移す。毎回の起動で呼ぶが、移すものが無ければ何も書かない
    /// 移し終えたらLauncher列は削除する。
    /// </summary>
    private static async Task MigrateRomFileCustomAsync(SqliteConnection connection)
    {
        List<string> columns = (await connection
            .QueryAsync<string>("SELECT name FROM pragma_table_info('RomFile');")
            .ConfigureAwait(false)).ToList();

        if (!columns.Contains("Custom", StringComparer.OrdinalIgnoreCase))
        {
            await connection.ExecuteAsync(
                "ALTER TABLE RomFile ADD COLUMN Custom TEXT NOT NULL DEFAULT '';").ConfigureAwait(false);
        }

        // 旧Launcher列が無ければ(新規DB・移行済み)移すものは無い。
        if (!columns.Contains("Launcher", StringComparer.OrdinalIgnoreCase)) return;

        IEnumerable<LauncherMigrationRow> pending = await connection.QueryAsync<LauncherMigrationRow>(
            "SELECT No, Launcher, Custom FROM RomFile WHERE Launcher IS NOT NULL AND Launcher <> '';")
            .ConfigureAwait(false);

        foreach (LauncherMigrationRow row in pending)
        {
            System.Text.Json.Nodes.JsonObject custom = EmulatorLauncher.Models.RomCustom.Parse(row.Custom);
            if (custom.ContainsKey(EmulatorLauncher.Models.RomCustom.LauncherKey)) continue;

            EmulatorLauncher.Models.RomCustom.SetLauncher(custom, row.Launcher ?? string.Empty);
            await connection.ExecuteAsync(
                "UPDATE RomFile SET Custom = @Custom WHERE No = @No;",
                new { Custom = EmulatorLauncher.Models.RomCustom.Serialize(custom), row.No }).ConfigureAwait(false);
        }

        // Customへ移し終えたので、旧Launcher列は削除する(SQLite 3.35以降のDROP COLUMN)。
        // 失敗しても、列はDEFAULT ''付きで残るだけで、アプリは使わないので問題ない。
        try
        {
            await connection.ExecuteAsync("ALTER TABLE RomFile DROP COLUMN Launcher;").ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// ユーザー定義のゲームまとめ(「お気に入り」等。コンソールをまたいでRomFileを集められる)。
    /// CollectionIdは"favorite"のような固定文字列/生成したスラッグで、表示名(Name)とは別に
    /// アプリ内部からの参照キーとして使う(名前を変更してもCollectionIdは変わらない)。
    /// </summary>
    private static async Task CreateRomCollectionTableAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            @"CREATE TABLE IF NOT EXISTS RomCollection (
                No INTEGER PRIMARY KEY AUTOINCREMENT,
                CollectionId TEXT NOT NULL,
                Name TEXT NOT NULL,
                IconPath TEXT NOT NULL DEFAULT '',
                AccentColorHex TEXT NOT NULL DEFAULT '#607D8B',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0
            );").ConfigureAwait(false);

        await connection.ExecuteAsync(
            @"CREATE UNIQUE INDEX IF NOT EXISTS IX_RomCollection_CollectionId
                ON RomCollection (CollectionId);").ConfigureAwait(false);
    }

    /// <summary>
    /// RomCollectionとRomFileの多対多の対応表(1本のゲームを複数のコレクションへ、
    /// 1つのコレクションに複数のゲームを、それぞれ登録できるようにするための橋渡しテーブル)。
    /// </summary>
    private static async Task CreateRomCollectionItemTableAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            @"CREATE TABLE IF NOT EXISTS RomCollectionItem (
                CollectionNo INTEGER NOT NULL,
                RomFileNo INTEGER NOT NULL,
                AddedAt TEXT NOT NULL,
                PRIMARY KEY (CollectionNo, RomFileNo),
                FOREIGN KEY (CollectionNo) REFERENCES RomCollection (No),
                FOREIGN KEY (RomFileNo) REFERENCES RomFile (No)
            );").ConfigureAwait(false);

        // 「このゲームはどのコレクションに入っているか」(ゲーム選択画面の詳細画面)を
        // 高速化するための索引。
        await connection.ExecuteAsync(
            @"CREATE INDEX IF NOT EXISTS IX_RomCollectionItem_RomFileNo
                ON RomCollectionItem (RomFileNo);").ConfigureAwait(false);
    }

    /// <summary>
    /// 複数枚組のゲーム(.m3u)と、そのメンバー(ディスク1・ディスク2…のRomFile)の対応表。
    /// SetNoが.m3uのRomFile.No、MemberNoがメンバーのRomFile.No、Positionが並び順(0始まり)。
    /// .m3uファイルの中身から作る「索引」で、壊れても再スキャン(RomDiscSetService.SyncFolderAsync)で作り直せる。
    /// メンバーは一覧から隠す(RomFileRepositoryの一覧取得が、この表を見て除外する)。
    /// 外部キーは付けない(RomFileが物理削除されても残った行は、SyncFolderAsyncが掃除する。
    /// 一覧からの除外は、セット側が有効なときだけ効くので、残った行があってもゲームは隠れない)。
    /// </summary>
    private static async Task CreateRomDiscMemberTableAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            @"CREATE TABLE IF NOT EXISTS RomDiscMember (
                SetNo INTEGER NOT NULL,
                Position INTEGER NOT NULL,
                MemberNo INTEGER NOT NULL,
                PRIMARY KEY (SetNo, Position)
            );").ConfigureAwait(false);

        await connection.ExecuteAsync(
            @"CREATE INDEX IF NOT EXISTS IX_RomDiscMember_MemberNo
                ON RomDiscMember (MemberNo);").ConfigureAwait(false);
    }

    /// <summary>
    /// 「お気に入り」コレクションが無ければ作成しておく。ユーザーがコレクションを新規作成する
    /// 画面をまだ持っていない現時点でも、ゲーム選択画面の詳細画面から常にお気に入り登録/解除が
    /// できるようにするための既定コレクション(削除不可を意図しているが、削除防止のUI制御は
    /// まだ実装していない)。SortOrder=-1にして一覧の先頭に来るようにしている。
    /// </summary>
    private static async Task SeedBuiltinCollectionsAsync(SqliteConnection connection)
    {
        // 同梱の既定アイコン(実行フォルダ配下のImages/System/01_favorite.png。コレクション編集のアイコン選択と同じ場所)。
        // 画像がまだ無くても、カードはプレースホルダー表示になるだけで動作には影響しない。
        string favoriteIcon = System.IO.Path.Combine(AppContext.BaseDirectory, "Images", "System", "01_favorite.png");

        await connection.ExecuteAsync(
            @"INSERT OR IGNORE INTO RomCollection (CollectionId, Name, IconPath, AccentColorHex, SortOrder)
                VALUES ('favorite', 'お気に入り', @Icon, '#E53950', -1);",
            new { Icon = favoriteIcon }).ConfigureAwait(false);

        // 既に作成済みのDBの「お気に入り」にも、アイコンが空なら1度だけ既定アイコンを入れる
        // (PRAGMA user_versionを「適用済み」の印に使う。後でユーザーが「なし」へ変えても上書きしない)。
        long version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;").ConfigureAwait(false);
        if (version < 1)
        {
            await connection.ExecuteAsync(
                @"UPDATE RomCollection SET IconPath = @Icon
                    WHERE CollectionId = 'favorite' AND IconPath = '';",
                new { Icon = favoriteIcon }).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA user_version = 1;").ConfigureAwait(false);
        }
    }
}
