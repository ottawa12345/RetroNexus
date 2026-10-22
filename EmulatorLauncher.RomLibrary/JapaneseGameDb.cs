using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EmulatorLauncher.RomLibrary;

/// <summary>日本語メタデータ表(data/jdb/{コンソールId}.csv)の1行分。</summary>
public sealed class JapaneseGameInfo
{
    public string Title { get; init; } = string.Empty;

    /// <summary>英語タイトル("enname"列。無ければ空)。</summary>
    public string EnglishTitle { get; init; } = string.Empty;

    /// <summary>タイトルのひらがな読み(並べ替え用。"kana"列。無ければ空)。</summary>
    public string Kana { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public string Players { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public int? ReleaseYear { get; init; }
    public uint? Crc { get; init; }
    public string Serial { get; init; } = string.Empty;
}

/// <summary>日本語のタイトル・概要などを持つCSV(data/jdb/{コンソールId}.csv。例: n64.csv)の読み込みと検索。</summary>
public static class JapaneseGameDb
{
    private sealed class Table
    {
        public Dictionary<uint, JapaneseGameInfo> ByCrc { get; } = new();
        public Dictionary<string, JapaneseGameInfo> BySerial { get; } = new(StringComparer.Ordinal);

        /// <summary>enname列(英語タイトル)をゆるく正規化した"タイトル|地域"キー → 行。</summary>
        public Dictionary<string, JapaneseGameInfo> ByName { get; } = new(StringComparer.Ordinal);
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Table> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static string GetPath(string consoleId)
        => Path.Combine(AppContext.BaseDirectory, "data", "jdb", consoleId + ".csv");

    /// <summary>このコンソール用の日本語表があるか(ファイルが存在し、1行以上読めたか)。</summary>
    public static bool HasTable(string consoleId)
    {
        Table table = Load(consoleId);
        return table.ByCrc.Count > 0 || table.BySerial.Count > 0 || table.ByName.Count > 0;
    }

    /// <summary>CRC-32、無ければSerialで日本語情報を探す。</summary>
    public static bool TryFind(string consoleId, uint? crc, string? serial, out JapaneseGameInfo info)
    {
        Table table = Load(consoleId);
        if (crc is uint c && table.ByCrc.TryGetValue(c, out JapaneseGameInfo? byCrc))
        {
            info = byCrc;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(serial) && table.BySerial.TryGetValue(NormalizeSerial(serial), out JapaneseGameInfo? bySerial))
        {
            info = bySerial;
            return true;
        }

        info = new JapaneseGameInfo();
        return false;
    }

    /// <summary>ファイル名(拡張子なし)で探す。</summary>
    public static bool TryFindByName(string consoleId, string? name, out JapaneseGameInfo info)
    {
        info = new JapaneseGameInfo();
        if (string.IsNullOrWhiteSpace(name)) return false;

        Table table = Load(consoleId);
        if (table.ByName.Count == 0) return false;

        (string title, string region, _) = RdbDatabase.SplitLooseName(name);
        if (title.Length == 0) return false;

        if (table.ByName.TryGetValue(title + "|" + region, out JapaneseGameInfo? hit))
        {
            info = hit;
            return true;
        }

        // 地域が省略されたファイル名("Splatoon.wua")は、地域の付いた行のどれかに当てる。
        if (region.Length == 0)
        {
            foreach (string r in new[] { "japan", "usa", "europe" })
            {
                if (table.ByName.TryGetValue(title + "|" + r, out hit))
                {
                    info = hit;
                    return true;
                }
            }
        }

        return false;
    }

    private static Table Load(string consoleId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(consoleId, out Table? cached)) return cached;
            var table = new Table();
            try
            {
                string path = GetPath(consoleId);
                if (File.Exists(path)) Fill(table, ReadCsv(DecodeText(File.ReadAllBytes(path))));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JapaneseGameDb] 読み込みに失敗しました({consoleId}): {ex.Message}");
            }

            Cache[consoleId] = table;
            return table;
        }
    }

    private static void Fill(Table table, List<string[]> rows)
    {
        if (rows.Count < 2) return;

        string[] header = rows[0];
        int Col(string name) => Array.FindIndex(header, h => string.Equals(h.Trim().TrimStart('﻿'), name, StringComparison.OrdinalIgnoreCase));
        int iName = Col("jpname"), iVer = Col("Version"), iDesc = Col("Description"), iGenre = Col("genre");
        int iRomName = Col("romname");
        int iKana = Col("kana"), iEn = Col("enname"), iUsers = Col("Users"), iRel = Col("Releases"), iPub = Col("Publisher"), iCrc = Col("CRC"), iSerial = Col("Serial");
        if (iName < 0) return;

        static string Get(string[] r, int i) => i >= 0 && i < r.Length ? r[i].Trim() : string.Empty;

        for (int n = 1; n < rows.Count; n++)
        {
            string[] r = rows[n];
            string title = Get(r, iName);
            if (title.Length == 0) continue;

            string users = Get(r, iUsers);
            var info = new JapaneseGameInfo
            {
                Title = title,
                Kana = Get(r, iKana),
                EnglishTitle = Get(r, iEn),
                Version = Get(r, iVer),
                Description = Get(r, iDesc).Replace("\\n", "\n"),
                Genre = ToJapaneseGenre(Get(r, iGenre)),
                Players = int.TryParse(users, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p > 0 ? $"{p}人用" : users,
                Publisher = Get(r, iPub),
                ReleaseYear = ParseYear(Get(r, iRel)),
                Crc = ParseCrc(Get(r, iCrc)),
                Serial = Get(r, iSerial),
            };

            if (info.Crc is uint crc) table.ByCrc.TryAdd(crc, info);

            // ファイル名で照合するコンソール用の鍵。
            string nameKey = Get(r, iRomName);
            if (nameKey.Length == 0) nameKey = info.EnglishTitle;
            if (nameKey.Length > 0)
            {
                (string nameTitle, string nameRegion, _) = RdbDatabase.SplitLooseName(nameKey);
                if (nameTitle.Length > 0) table.ByName.TryAdd(nameTitle + "|" + nameRegion, info);
            }

            if (info.Serial.Length > 0)
            {
                // 同じSerialの行が複数ある(リビジョン違い)ときは、Versionが空の行(初版)をSerial照合の代表にする。
                string serialKey = NormalizeSerial(info.Serial);
                if (!table.BySerial.TryGetValue(serialKey, out JapaneseGameInfo? existing)
                    || (existing.Version.Length > 0 && info.Version.Length == 0))
                {
                    table.BySerial[serialKey] = info;
                }
            }
        }
    }

    /// <summary>CSVのgenre列(英語)を、画面に出す日本語のジャンル名へ変える。</summary>
    private static string ToJapaneseGenre(string genre) => genre switch
    {
        "Action" => "アクション",
        "Adventure" => "アドベンチャー",
        "Board" => "ボードゲーム",
        "Casual Game" => "カジュアル",
        "Compilation" => "コンピレーション",
        "Fighting" => "対戦格闘",
        "Gambling" => "ギャンブル",
        "Hunting and Fishing" => "釣り・狩猟",
        "Music / Dancing" => "音楽・ダンス",
        "Platform" => "プラットフォーム",
        "Puzzle" => "パズル",
        "Racing" => "レース",
        "Role-playing (RPG)" => "RPG",
        "Shoot'em Up" => "シューティング",
        "Shooter" => "シューティング",
        "Simulation" => "シミュレーション",
        "Sports" => "スポーツ",
        "Sports with Animals" => "動物スポーツ",
        "Strategy" => "ストラテジー",
        "Various" => "その他",
        _ => genre,
    };

    /// <summary>"SLUS-00908"と"SLUS_009.08"のような表記の違いを吸収するため、英数字だけを大文字にして比べる。</summary>
    private static string NormalizeSerial(string serial)
        => new string(serial.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();

    private static int? ParseYear(string releases)
    {
        Match m = Regex.Match(releases, @"^(\d{2,4})(?:/|$)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int y)) return null;
        if (y >= 1000) return y;
        return y >= 70 ? 1900 + y : 2000 + y;
    }

    private static uint? ParseCrc(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];

        // Excelが"57767E49"を指数表記("5.7767E+049")に変えてしまったものを戻す。
        Match sci = Regex.Match(text, @"^(\d+)\.?(\d*)E\+0*(\d+)$", RegexOptions.IgnoreCase);
        if (sci.Success) text = sci.Groups[1].Value + sci.Groups[2].Value + "E" + sci.Groups[3].Value;

        if (text.Length is 0 or > 8 || !Regex.IsMatch(text, "^[0-9A-Fa-f]+$")) return null;
        // 先頭の0がExcelで落ちている場合に備え、8桁に揃える。
        return uint.Parse(text.PadLeft(8, '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    /// <summary>最小限のCSV(RFC4180)読み込み。</summary>
    private static List<string[]> ReadCsv(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        bool any = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; any = true; break;
                case ',': fields.Add(sb.ToString()); sb.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    if (any || sb.Length > 0)
                    {
                        fields.Add(sb.ToString());
                        rows.Add(fields.ToArray());
                    }

                    fields.Clear(); sb.Clear(); any = false;
                    break;
                default: sb.Append(c); any = true; break;
            }
        }

        if (any || sb.Length > 0)
        {
            fields.Add(sb.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }
}
