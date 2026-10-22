namespace EmulatorLauncher.Models;

/// <summary>RetroArch RDB(libretro-database)から読み取った1タイトル分の情報。</summary>
public class RdbGameEntry
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Genre { get; set; }
    public int? ReleaseYear { get; set; }
    public string? Developer { get; set; }
    public string? Users { get; set; }

    /// <summary>シリアル(製品コード/タイトルID)。</summary>
    public string? Serial { get; set; }

    /// <summary>このタイトルのROM(展開済み/圧縮前)のCRC-32。</summary>
    public uint? Crc { get; set; }

    /// <summary>RDBの"rom_name"(例: "1941.zip")。</summary>
    public string? RomName { get; set; }
}