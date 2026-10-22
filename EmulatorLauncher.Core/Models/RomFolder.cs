namespace EmulatorLauncher.Models;

/// <summary>ROMスキャン対象として登録した1フォルダ分の管理情報(RomFolderテーブルに対応)。</summary>
public class RomFolder
{
    /// <summary>SQLite側でAUTOINCREMENT採番される一意な番号。</summary>
    public int No { get; set; }

    /// <summary>NTFS上のフォルダ識別子。</summary>
    public Guid FolderId { get; set; }

    /// <summary>NTFSボリュームのシリアル番号。</summary>
    public ulong VolumeSerial { get; set; }

    /// <summary>フォルダ自体の最終更新日時(ファイルシステム側の更新日時)。</summary>
    public DateTime LastModifyTimeAt { get; set; }

    /// <summary>このフォルダを最後にスキャンした日時。</summary>
    public DateTime LastScannedAt { get; set; }

    /// <summary>このフォルダに対応するコンソールのId("fc"、"sfc"等。ConsoleInfo.Idと同じ)。</summary>
    public string ConsoleId { get; set; } = string.Empty;

    /// <summary>フォルダの絶対パス。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>論理削除フラグ。</summary>
    public bool IsDeleted { get; set; } = false;
}
