using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using EmulatorLauncher.Models;
using MessagePack;

namespace EmulatorLauncher.RomLibrary;

/// <summary>RDBを読み込んだ結果。</summary>
public sealed class RdbDatabase
{
    /// <summary>"SLUS00908GH"のように、数字の後ろに付いた英字の接尾辞(廉価版等)を取り除くための正規表現。</summary>
    private static readonly Regex SerialSuffixPattern = new(
        @"^(.*\d)[A-Z]{1,4}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>CRC-32 → エントリ。</summary>
    public Dictionary<uint, RdbGameEntry> ByCrc { get; } = new();

    /// <summary>正規化済みシリアル() → エントリ。</summary>
    public Dictionary<string, RdbGameEntry> BySerial { get; } = new(StringComparer.Ordinal);

    /// <summary>接尾辞を取り除いたシリアル → エントリ。</summary>
    private readonly Dictionary<string, RdbGameEntry> _bySerialBase = new(StringComparer.Ordinal);

    /// <summary>拡張子を除いたROMファイル名(大文字小文字を区別しない) → エントリ。</summary>
    public Dictionary<string, RdbGameEntry> ByRomBaseName { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>シリアルの表記ゆれを吸収するための正規化(大文字化し、英数字以外を除く)。</summary>
    public static string NormalizeSerial(string serial)
    {
        var builder = new StringBuilder(serial.Length);
        foreach (char c in serial)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>RDBのserial値を登録する。</summary>
    internal void AddSerial(string rawSerial, RdbGameEntry entry)
    {
        foreach (string part in rawSerial.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string key = NormalizeSerial(part);
            if (key.Length == 0)
            {
                continue;
            }

            BySerial.TryAdd(key, entry);

            Match match = SerialSuffixPattern.Match(key);
            if (match.Success)
            {
                _bySerialBase.TryAdd(match.Groups[1].Value, entry);
            }
        }
    }

    /// <summary>ディスクから読み取ったシリアルでエントリを探す。</summary>
    public bool TryFindBySerial(string serial, [NotNullWhen(true)] out RdbGameEntry? entry)
    {
        string key = NormalizeSerial(serial);
        if (key.Length == 0)
        {
            entry = null;
            return false;
        }

        return BySerial.TryGetValue(key, out entry) || _bySerialBase.TryGetValue(key, out entry);
    }

    private readonly Dictionary<string, List<RdbGameEntry>> _namedBySerial = new(StringComparer.Ordinal);

    /// <summary>同じシリアルのエントリが地域違いで複数ある(3DSの"CTR-P-ECRA"は日本・韓国・台湾・北米で共通)ときに、ファイル名の地域に合うものを選ぶ。</summary>
    public bool TryFindBySerialForFile(string serial, string fileBaseName, [NotNullWhen(true)] out RdbGameEntry? entry)
    {
        entry = null;
        if (!_namedBySerial.TryGetValue(NormalizeSerial(serial), out List<RdbGameEntry>? list) || list.Count == 0) return false;

        string fileRegion = SplitLooseName(fileBaseName).Region;
        IEnumerable<RdbGameEntry> pool = list;
        if (fileRegion.Length > 0)
        {
            List<RdbGameEntry> sameRegion = list.Where(e => SplitLooseName(e.Name!).Region == fileRegion).ToList();
            if (sameRegion.Count > 0) pool = sameRegion;
        }

        entry = pool
            .OrderBy(e => SplitLooseName(e.Name!).Region == "japan" ? 0 : 1)
            .ThenBy(e => SplitLooseName(e.Name!).ExtraTags)
            .ThenBy(e => e.Name!.Length)
            .First();
        return true;
    }

    private readonly Dictionary<string, RdbGameEntry> _byGameCode = new(StringComparer.Ordinal);

    /// <summary>GBA/NDSのヘッダのゲームコード(4文字)でエントリを探す。</summary>
    public bool TryFindByGameCode(string code, [NotNullWhen(true)] out RdbGameEntry? entry)
        => _byGameCode.TryGetValue(code.ToUpperInvariant(), out entry);

    /// <summary>名前の無いエントリ(CRCとシリアルだけのエントリ)の件数(読み込み後に設定される)。</summary>
    public int NamelessCount { get; private set; }

    /// <summary>名前の無いエントリのうち、同じシリアルを持つ名前付きエントリから名前を補えた件数。</summary>
    public int BorrowedNameCount { get; private set; }

    /// <summary>"KDS-AIN"のような「3文字-3文字」のシリアル(ファミコンディスクシステム等)。</summary>
    private static readonly Regex PrefixedSerialPattern = new(
        @"^[A-Z]{3}-([A-Z0-9]{3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>読み込み後の仕上げ。</summary>
    internal void FinishLoad(List<(RdbGameEntry Entry, string? Serial)> all)
    {
        // GBA/NDS: 4文字のゲームコード → 代表エントリ(付記の少ないもの、次に名前の短いもの)。
        var byCode = new Dictionary<string, List<RdbGameEntry>>(StringComparer.Ordinal);
        foreach ((RdbGameEntry entry, string? serial) in all)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || serial is null || serial.Length != 4) continue;
            string code = serial.ToUpperInvariant();
            if (!byCode.TryGetValue(code, out List<RdbGameEntry>? codeList))
            {
                codeList = new List<RdbGameEntry>();
                byCode[code] = codeList;
            }
            codeList.Add(entry);
        }

        foreach ((string code, List<RdbGameEntry> list) in byCode)
        {
            _byGameCode[code] = list
                .OrderBy(d => SplitLooseName(d.Name!).ExtraTags)
                .ThenBy(d => d.Name!.Length)
                .First();
        }

        var namedBySerial = _namedBySerial;
        foreach ((RdbGameEntry entry, string? serial) in all)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(serial)) continue;
            foreach (string part in serial.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string key = NormalizeSerial(part);
                if (key.Length == 0) continue;
                if (!namedBySerial.TryGetValue(key, out List<RdbGameEntry>? list))
                {
                    list = new List<RdbGameEntry>();
                    namedBySerial[key] = list;
                }
                list.Add(entry);
            }
        }

        foreach ((RdbGameEntry entry, string? serial) in all)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name)) continue;
            NamelessCount++;
            if (string.IsNullOrWhiteSpace(serial)) continue;

            foreach (string part in serial.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = PrefixedSerialPattern.Match(part.Trim().ToUpperInvariant());
                if (!match.Success) continue;
                if (!namedBySerial.TryGetValue(NormalizeSerial(match.Groups[1].Value), out List<RdbGameEntry>? donors)) continue;

                RdbGameEntry donor = donors
                    .OrderBy(d => SplitLooseName(d.Name!).ExtraTags)
                    .ThenBy(d => d.Name!.Length)
                    .First();

                entry.Name = donor.Name;
                if (string.IsNullOrEmpty(entry.Description)) entry.Description = donor.Description;
                if (string.IsNullOrEmpty(entry.Genre)) entry.Genre = donor.Genre;
                if (entry.ReleaseYear is null) entry.ReleaseYear = donor.ReleaseYear;
                if (string.IsNullOrEmpty(entry.Developer)) entry.Developer = donor.Developer;
                if (string.IsNullOrEmpty(entry.Users)) entry.Users = donor.Users;
                BorrowedNameCount++;
                break;
            }
        }
    }

    private Dictionary<string, List<LooseKey>>? _looseByTitle;

    private static readonly Regex DiscTagPattern = new(@"\(Disc (\d+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>ファイル名の「簡略キー」(小文字・英数字だけ・括弧内を除く)がRDBの名前と一致するエントリを探す。</summary>
    public bool TryFindByCompactName(string fileBaseName, [NotNullWhen(true)] out RdbGameEntry? entry)
    {
        entry = null;
        if (_looseList.Count == 0) return false;

        if (_looseByTitle is null)
        {
            var map = new Dictionary<string, List<LooseKey>>(StringComparer.Ordinal);
            foreach (LooseKey item in _looseList)
            {
                if (!map.TryGetValue(item.Title, out List<LooseKey>? list))
                {
                    list = new List<LooseKey>();
                    map[item.Title] = list;
                }
                list.Add(item);
            }
            _looseByTitle = map;
        }

        string key = SplitLooseName(fileBaseName).Title;
        if (key.Length == 0 || !_looseByTitle.TryGetValue(key, out List<LooseKey>? candidates)) return false;

        // ファイル名に地域の記載があるときは、同じ地域の候補だけを対象にする("(J)"のファイルを(France)等の別地域のエントリに結び付けない)。
        string fileRegion = SplitLooseName(fileBaseName).Region;
        if (fileRegion.Length > 0)
        {
            candidates = candidates.Where(c => c.Region == fileRegion).ToList();
            if (candidates.Count == 0) return false;
        }

        // 複数ディスク: ファイル名に"(Disc N)"があるときは、同じディスク番号の候補だけを対象にする。
        Match fileDisc = DiscTagPattern.Match(fileBaseName);
        if (fileDisc.Success)
        {
            string disc = fileDisc.Groups[1].Value;
            candidates = candidates
                .Where(c => DiscTagPattern.Match(c.Entry.Name ?? c.Entry.RomName ?? "") is { Success: true } m && m.Groups[1].Value == disc)
                .ToList();
            if (candidates.Count == 0) return false;
        }

        LooseKey best = candidates
            .OrderBy(c => c.Region == "japan" ? 0 : 1)
            .ThenBy(c => c.ExtraTags)
            .ThenBy(c => (c.Entry.Name ?? c.Entry.RomName ?? "").Length)
            .First();
        entry = best.Entry;
        return true;
    }

    /// <summary>RDBの"rom_name"(例: "1941.zip")を登録する。</summary>
    internal void AddRomName(string romName, RdbGameEntry entry)
    {
        string baseName = Path.GetFileNameWithoutExtension(romName);
        if (baseName.Length == 0)
        {
            return;
        }

        ByRomBaseName.TryAdd(baseName, entry);
        AddLooseName(baseName, entry);
    }

    // ゆるい名前照合(ファイル名が"(JP)"や"(v272)"など、DATの正式名と少し違う場合用)

    private sealed record LooseKey(string Title, string Region, int ExtraTags, RdbGameEntry Entry);

    private readonly Dictionary<string, LooseKey> _loose = new(StringComparer.Ordinal);
    private readonly List<LooseKey> _looseList = new();

    private static readonly Regex TagGroupPattern = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>名前を「タイトル部分(英数字だけ・小文字・アクセント除去)」と「地域(japan/usa/europe/空)」に分ける。</summary>
    internal static (string Title, string Region, int ExtraTags) SplitLooseName(string name)
    {
        string region = "";
        int extra = 0;
        foreach (Match m in TagGroupPattern.Matches(name))
        {
            string inner = m.Value.Trim('(', ')', '[', ']').Trim().ToLowerInvariant();
            // "(Japan, USA)"のようなカンマ区切りは、最初に地域として読めた要素を使う。
            string? r = null;
            foreach (string token in inner.Split(','))
            {
                r = token.Trim() switch
                {
                    "jp" or "japan" or "jpn" or "j" => "japan",
                    "us" or "usa" or "u" => "usa",
                    "eu" or "europe" or "eur" or "e" => "europe",
                    _ => null,
                };
                if (r is not null) break;
            }
            if (r is not null) { if (region.Length == 0) region = r; }
            else extra++;
        }

        string stripped = TagGroupPattern.Replace(name, " ");
        string decomposed = stripped.Normalize(NormalizationForm.FormKD).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }

        return (sb.ToString(), region, extra);
    }

    private void AddLooseName(string baseName, RdbGameEntry entry)
    {
        (string title, string region, int extra) = SplitLooseName(baseName);
        if (title.Length == 0) return;

        var item = new LooseKey(title, region, extra, entry);
        string key = title + "|" + region;
        // 同じタイトル・地域が複数ある場合は、付記の少ない(=デモ/体験版でない)方を優先する。
        if (!_loose.TryGetValue(key, out LooseKey? existing) || extra < existing.ExtraTags)
        {
            _loose[key] = item;
        }
        _looseList.Add(item);
    }

    private bool TryFindLoose(string baseName, [NotNullWhen(true)] out RdbGameEntry? entry)
    {
        entry = null;
        if (_loose.Count == 0) return false;

        (string title, string region, _) = SplitLooseName(baseName);
        if (title.Length == 0) return false;

        // 1) タイトル+地域が一致
        if (_loose.TryGetValue(title + "|" + region, out LooseKey? exact))
        {
            entry = exact.Entry;
            return true;
        }

        // 地域の付いていない名前(アーケード等)ではこれ以上あいまい照合しない。
        if (region.Length == 0) return false;

        // 2) "Taiko no Tatsujin Atsumete... (JP)" のように"..."で省略された名前: 前方一致
        int ellipsis = baseName.IndexOf("...", StringComparison.Ordinal);
        if (ellipsis > 0)
        {
            (string prefix, _, _) = SplitLooseName(baseName[..ellipsis]);
            if (prefix.Length >= 6)
            {
                LooseKey? hit = _looseList
                    .Where(k => k.Region == region && k.Title.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(k => k.ExtraTags).ThenBy(k => k.Title.Length).FirstOrDefault();
                if (hit is not null) { entry = hit.Entry; return true; }
            }
        }

        // 3) 正式名の一部(前方/部分一致)。
        if (title.Length >= 8)
        {
            LooseKey? hit = _looseList
                .Where(k => k.Region == region && k.Title.Contains(title, StringComparison.Ordinal))
                .OrderBy(k => k.Title.StartsWith(title, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(k => k.ExtraTags).ThenBy(k => k.Title.Length).FirstOrDefault();
            if (hit is not null) { entry = hit.Entry; return true; }
        }

        return false;
    }

    /// <summary>ファイル名(拡張子を除いた部分)でエントリを探す。</summary>
    public bool TryFindByRomBaseName(string baseName, [NotNullWhen(true)] out RdbGameEntry? entry)
        => ByRomBaseName.TryGetValue(baseName, out entry) || TryFindLoose(baseName, out entry);
}

/// <summary>RetroArch(libretro-database)のRDBファイルを読み込み、CRC-32およびシリアルをキーにしたタイトル情報の辞書を構築する。</summary>
public static class RetroArchRdbReader
{
    // 実機ダンプで確認できた「RARCHDB」7文字部分だけを厳密にチェックする(8byte目はrdbによって値が異なる可能性があるため問わない)。
    private static readonly byte[] MagicBytes = "RARCHDB"u8.ToArray();
    private const int HeaderSize = 16;

    /// <summary>rdbファイルを読み込み、CRC-32をキーにしたエントリ辞書を返す(従来互換)。</summary>
    public static async Task<Dictionary<uint, RdbGameEntry>> LoadAsync(string rdbPath)
        => (await LoadDatabaseAsync(rdbPath).ConfigureAwait(false)).ByCrc;

    /// <summary>rdbファイルを読み込み、CRC-32とシリアルの両方をキーにしたを返す。</summary>
    public static async Task<RdbDatabase> LoadDatabaseAsync(string rdbPath)
    {
        var result = new RdbDatabase();
        var allEntries = new List<(RdbGameEntry Entry, string? Serial)>();

        try
        {
            await using FileStream stream = File.OpenRead(rdbPath);

            byte[] header = new byte[HeaderSize];
            int read = await stream.ReadAsync(header.AsMemory(0, HeaderSize)).ConfigureAwait(false);
            if (read < HeaderSize || !header.AsSpan(0, MagicBytes.Length).SequenceEqual(MagicBytes))
            {
                System.Diagnostics.Debug.WriteLine($"[RetroArchRdbReader] マジック文字列が一致しません: {rdbPath}");
                return result;
            }

            using var msgPackReader = new MessagePackStreamReader(stream);
            while (await msgPackReader.ReadAsync(CancellationToken.None).ConfigureAwait(false) is { } sequence)
            {
                RdbGameEntry? entry = ParseEntry(sequence, out string? serial);
                if (entry is null)
                {
                    continue;
                }

                allEntries.Add((entry, serial));

                if (entry.Crc is uint crc)
                {
                    // 同じCRCが複数回登場する場合(リージョン違いの重複データ等)は最初に見つかった1件を採用する。
                    result.ByCrc.TryAdd(crc, entry);
                }

                if (!string.IsNullOrWhiteSpace(serial))
                {
                    result.AddSerial(serial, entry);
                }

                if (!string.IsNullOrWhiteSpace(entry.RomName))
                {
                    result.AddRomName(entry.RomName, entry);
                }
            }

            // 名前の無いエントリへ、同じシリアルの名前付きエントリから名前を補う。
            result.FinishLoad(allEntries);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RetroArchRdbReader] RDBの読み込みに失敗しました({rdbPath}): {ex.Message}");
        }

        return result;
    }

    /// <summary>1エントリ分のmapを読む。</summary>
    private static RdbGameEntry? ParseEntry(ReadOnlySequence<byte> sequence, out string? serial)
    {
        serial = null;
        var reader = new MessagePackReader(sequence);
        if (reader.NextMessagePackType != MessagePackType.Map)
        {
            return null; // 想定外の形式のエントリはスキップする
        }

        int count = reader.ReadMapHeader();
        var entry = new RdbGameEntry();

        for (int i = 0; i < count; i++)
        {
            string? key = reader.ReadString();
            object? value = ReadValue(ref reader);

            switch (key)
            {
                case "name":
                    entry.Name = value as string;
                    break;
                case "description":
                    entry.Description = value as string;
                    break;
                case "genre":
                    entry.Genre = value as string;
                    break;
                case "releaseyear":
                    entry.ReleaseYear = ToInt(value);
                    break;
                case "developer":
                    entry.Developer = value as string;
                    break;
                case "users":
                    entry.Users = value switch
                    {
                        null => null,
                        string s => s,
                        _ => ToInt(value)?.ToString(CultureInfo.InvariantCulture),
                    };
                    break;
                case "crc":
                    entry.Crc = ToCrc(value);
                    break;
                case "serial":
                    serial = ToSerial(value);
                    break;
                case "rom_name":
                    entry.RomName = value as string;
                    break;
            }
        }

        return entry;
    }

    /// <summary>MessagePackの1値を、型を決め打ちせず素直な.NETの値(string/long/double/byte[]/bool/ null、もしくは配列/辞書)へ変換する。</summary>
    private static object? ReadValue(ref MessagePackReader reader)
    {
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Nil:
                reader.ReadNil();
                return null;
            case MessagePackType.Boolean:
                return reader.ReadBoolean();
            case MessagePackType.Integer:
                // 符号・サイズを問わずいったんlongで受ける(RDBの数値項目はいずれもこの範囲に収まる)。
                return reader.ReadInt64();
            case MessagePackType.Float:
                return reader.ReadDouble();
            case MessagePackType.String:
                return reader.ReadString();
            case MessagePackType.Binary:
                {
                    ReadOnlySequence<byte>? bin = reader.ReadBytes();
                    return bin?.ToArray();
                }
            case MessagePackType.Array:
                {
                    int len = reader.ReadArrayHeader();
                    var list = new object?[len];
                    for (int i = 0; i < len; i++) list[i] = ReadValue(ref reader);
                    return list;
                }
            case MessagePackType.Map:
                {
                    int len = reader.ReadMapHeader();
                    var map = new Dictionary<string, object?>();
                    for (int i = 0; i < len; i++)
                    {
                        string k = reader.ReadString() ?? string.Empty;
                        map[k] = ReadValue(ref reader);
                    }
                    return map;
                }
            default:
                reader.Skip();
                return null;
        }
    }

    /// <summary>serialエントリの値を文字列へ変換する。</summary>
    private static string? ToSerial(object? value) => value switch
    {
        byte[] bytes => Encoding.ASCII.GetString(bytes).TrimEnd('\0').Trim(),
        string s => s.Trim(),
        _ => null,
    };

    private static int? ToInt(object? value) => value switch
    {
        null => null,
        long l => (int)l,
        int i => i,
        double d => (int)d,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        _ => null,
    };

    /// <summary>crcエントリの値をuint32へ変換する。</summary>
    private static uint? ToCrc(object? value) => value switch
    {
        null => null,
        byte[] bytes when bytes.Length == 4 =>
            (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]),
        long l => unchecked((uint)l),
        int i => unchecked((uint)i),
        string s when uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) => parsed,
        _ => null,
    };
}