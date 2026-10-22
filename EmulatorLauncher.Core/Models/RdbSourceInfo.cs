using System.Text.Json.Serialization;

namespace EmulatorLauncher.Models;

/// <summary>data/rdb/rdblist.json の1エントリ。</summary>
public class RdbSourceInfo
{
    /// <summary>対応するコンソールのId("fc"、"sfc"等。ConsoleInfo.Idと同じ)。</summary>
    public string ConsoleId { get; set; } = string.Empty;

    /// <summary>data/rdbフォルダに保存するファイル名(例: "n64.rdb")。</summary>
    public string Rdb { get; set; } = string.Empty;

    /// <summary>rdbファイルのダウンロード元URL(libretro-databaseリポジトリのrdbファイル)。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>RDBが無いコンソール向けの、libretro-databaseの"dat"フォルダのDATファイル(clrmamepro形式)のダウンロード元URL。</summary>
    [JsonPropertyName("dat")]
    public string Dat { get; set; } = string.Empty;

    /// <summary>画像ファイルの拡張子(".png"/".jpg")。</summary>
    [JsonPropertyName("imageExt")]
    public string ImageExtension { get; set; } = ".png";

    /// <summary>trueにすると、そのコンソールのフォルダをスキャンする最初に、"list"にある画像を全て(並列で)data/rdb/images/{ConsoleId}/へまとめてダウンロードしておき、各ゲームの画像はそこからコピーする。</summary>
    [JsonPropertyName("bulk")]
    public bool Bulk { get; set; }

    /// <summary>ボックスアート画像のベースURL。</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>libretro-thumbnails側に実在する画像ファイルの一覧を取得できるURL (GitHub Contents APIのフォルダ一覧。例: ".../contents/Named_Boxarts?ref=...")。</summary>
    [JsonPropertyName("list")]
    public string ImageList { get; set; } = string.Empty;

    /// <summary>このコンソールでスキャン対象とするファイル拡張子("."始まり、例: ".z64")。</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>ROMファイルの照合方法。</summary>
    [JsonPropertyName("matchBy")]
    public string? MatchBy { get; set; }

    /// <summary>ボックスアートの基本の向き。</summary>
    [JsonPropertyName("boxArtOrientation")]
    public string? BoxArtOrientation { get; set; }

    /// <summary>向きが逆の画像を回転する角度(度)。</summary>
    [JsonPropertyName("boxArtRotation")]
    public int? BoxArtRotation { get; set; }
}
