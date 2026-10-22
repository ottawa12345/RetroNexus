using System.Buffers.Binary;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CHDSharp;
using CHDSharp.Models;

namespace EmulatorLauncher.RomLibrary;

/// <summary>ディスク系コンソールごとの「シリアル(製品番号)の取り出し方」。</summary>
public enum DiscSerialMethod
{
    /// <summary>シリアル照合を行わない(カートリッジ系。従来通りCRC-32のみで照合する)。</summary>
    None,

    /// <summary>PlayStation: ISO9660ルートのSYSTEM.CNFの"BOOT"行(例: cdrom:\SLUS_009.08;1)。</summary>
    PlayStation,

    /// <summary>PlayStation 2: ISO9660ルートのSYSTEM.CNFの"BOOT2"行(例: cdrom0:\SLUS_200.62;1)。</summary>
    PlayStation2,

    /// <summary>PSP: ISO9660ルートのUMD_DATA.BINの先頭("ULUS-10041|..."の'|'まで)。</summary>
    PlayStationPortable,

    /// <summary>セガサターン: 先頭セクタ(IP.BIN)のオフセット0x20にある10byteの製品番号。</summary>
    SegaSaturn,

    /// <summary>メガCD: 先頭セクタのオフセット0x180にある14byteのシリアル欄("GM T-xxxxx -00"形式)。</summary>
    SegaCd,

    /// <summary>GameCube: ディスクイメージ先頭0x00-0x05のGame ID(6文字、例: "GALE01")。</summary>
    GameCube,

    /// <summary>Wii: GameCubeと共通のディスクヘッダ形式で、先頭0x00-0x05のGame IDは同じ位置。</summary>
    Wii,

    /// <summary>ドリームキャスト: IP.BIN(先頭16byteが"SEGA SEGAKATANA ")のオフセット0x40にある10byteの製品番号("HDR-nnnn"等)。</summary>
    Dreamcast,

    /// <summary>ディスク系だが、ディスク上に頼れる埋め込みシリアルが存在しないコンソール(PCエンジンCD等。ブート領域にあるのはディスクタイトル文字列だけで、PS1のSYSTEM.CNFやGameCubeのGame IDに相当する構造化されたシリアルが無い)。</summary>
    CrcOnly,
}

/// <summary>CD/DVD等のディスクイメージからシリアル(製品番号)を取り出す。</summary>
public static class DiscSerialReader
{
    private const int CookedSectorSize = 2048;
    private const int RawSectorSize = 2352;

    /// <summary>zip内のイメージを読む際に展開する最大サイズ。</summary>
    private const int ZipPrefixBufferLimit = 32 * 1024 * 1024;

    /// <summary>SYSTEM.CNF/UMD_DATA.BIN等として読む小さなファイルの上限サイズ。</summary>
    private const int MaxSystemFileSize = 64 * 1024;

    /// <summary>ルートディレクトリとして読む最大セクタ数(壊れたイメージで暴走しないための上限)。</summary>
    private const int MaxRootDirectorySectors = 64;

    /// <summary>生セクタ(2352byte)先頭の同期パターン。</summary>
    private static readonly byte[] SyncPattern =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
    };

    /// <summary>zip内でディスクイメージ本体とみなす拡張子。</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".bin", ".img", ".pbp",
    };

    /// <summary>"SLUS_009.08" / "SLUS-00908" / "SLUS00908" 形式のブート実行ファイル名。</summary>
    private static readonly Regex BootExecutablePattern = new(
        @"^([A-Z]{4})[_-]?(\d{3})\.?(\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"SLUS00908" / "SLUS-00908" 形式(PARAM.SFOのDISC_ID、UMD_DATA.BIN)。</summary>
    private static readonly Regex CompactSerialPattern = new(
        @"^([A-Z]{4})-?(\d{5})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>consoleIdから、シリアルの取り出し方法を決める。</summary>
    public static DiscSerialMethod GetMethod(string consoleId) => consoleId.ToLowerInvariant() switch
    {
        "ps1" or "psx" => DiscSerialMethod.PlayStation,
        "ps2" => DiscSerialMethod.PlayStation2,
        "psp" => DiscSerialMethod.PlayStationPortable,
        "stn" or "saturn" => DiscSerialMethod.SegaSaturn,
        "segacd" or "megacd" => DiscSerialMethod.SegaCd,
        "gc" or "gamecube" or "ngc" => DiscSerialMethod.GameCube,
        "wii" => DiscSerialMethod.Wii,
        "dc" or "dreamcast" => DiscSerialMethod.Dreamcast,
        "pcecd" => DiscSerialMethod.CrcOnly,
        _ => DiscSerialMethod.None,
    };

    /// <summary>このコンソールが埋め込みシリアルの読み取りに対応しているか。</summary>
    public static bool UsesSerial(string consoleId) => GetMethod(consoleId) is not (DiscSerialMethod.None or DiscSerialMethod.CrcOnly);

    /// <summary>このコンソールがディスク系か(シリアルの有無を問わない)。</summary>
    public static bool IsDiscBased(string consoleId) => GetMethod(consoleId) != DiscSerialMethod.None;

    /// <summary>ディスク上のファイル(.cue/.bin/.iso/.pbp等)からシリアルを読み取る。</summary>
    public static Task<string?> ReadSerialAsync(string consoleId, string filePath)
    {
        DiscSerialMethod method = GetMethod(consoleId);
        if (method is DiscSerialMethod.None or DiscSerialMethod.CrcOnly)
        {
            return Task.FromResult<string?>(null);
        }

        // 数セクタ読むだけだが、同期I/Oなのでスレッドプールで実行する。
        return Task.Run(() => ReadSerialFromFile(method, filePath));
    }

    /// <summary>zip内のディスクイメージからシリアルを読み取る。</summary>
    public static string? ReadSerialFromZip(string consoleId, ZipArchive archive, out ZipArchiveEntry? dataEntry)
    {
        dataEntry = null;
        DiscSerialMethod method = GetMethod(consoleId);
        if (method is DiscSerialMethod.None or DiscSerialMethod.CrcOnly)
        {
            return null;
        }

        try
        {
            dataEntry = FindDataEntryInZip(archive);
            if (dataEntry is null)
            {
                return null;
            }

            using MemoryStream buffer = ReadPrefix(dataEntry, ZipPrefixBufferLimit);
            return ReadSerialFromStream(method, buffer, Path.GetExtension(dataEntry.FullName));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiscSerialReader] zip内イメージの読み取りに失敗しました: {ex.Message}");
            return null;
        }
    }

    /// <summary>シリアルが読み取れなかった場合に、CRC-32を計算すべきファイルを返す。</summary>
    public static string? GetCrcTargetPath(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".cue" => FindDataTrackFile(filePath),
            ".pbp" or ".chd" or ".cso" or ".ecm"
                or ".rvz" or ".gcz" or ".wbfs" or ".wia"
                or ".gdi" or ".cdi" => null,
            _ => filePath,
        };
    }

    /// <summary>.cueが参照している全ファイル(FILE行)の絶対パスを返す。</summary>
    public static IReadOnlyList<string> GetCueReferencedFiles(string cuePath)
    {
        try
        {
            string directory = Path.GetDirectoryName(cuePath) ?? string.Empty;
            return EnumerateCueFileNames(File.ReadAllText(cuePath))
                .Select(name => Path.GetFullPath(Path.Combine(directory, name)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiscSerialReader] cueの読み込みに失敗しました({cuePath}): {ex.Message}");
            return Array.Empty<string>();
        }
    }

    // 入口(ファイル/ストリーム → コンソール別の読み取り処理へ分岐)

    private static string? ReadSerialFromFile(DiscSerialMethod method, string filePath)
    {
        try
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            if (method == DiscSerialMethod.Dreamcast)
            {
                // ドリームキャストは.gdi/.chdのどちらも「トラック3の先頭セクタ」を読む必要があり、他のコンソールのような単純な単一ファイル読み取りに当てはまらないため、専用の入口を使う。
                return ReadDreamcastSerial(filePath, extension);
            }

            if (extension == ".chd")
            {
                return ReadSerialFromChd(method, filePath);
            }

            string? imagePath = extension switch
            {
                ".cue" => FindDataTrackFile(filePath),
                ".cso" or ".ecm" => null, // 未対応の圧縮形式
                _ => filePath,
            };

            if (imagePath is null)
            {
                return null;
            }

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadSerialFromStream(method, stream, Path.GetExtension(imagePath));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiscSerialReader] シリアルの読み取りに失敗しました({filePath}): {ex.Message}");
            return null;
        }
    }

    /// <summary>.chd(MAME Compressed Hunks of Data)からシリアルを読む。</summary>
    private static string? ReadSerialFromChd(DiscSerialMethod method, string filePath)
    {
        ChdError openError = ChdFile.Open(filePath, out ChdFile? chd);
        if (openError != ChdError.Chderrnone || chd is null)
        {
            System.Diagnostics.Debug.WriteLine($"[DiscSerialReader] .chdを開けませんでした({filePath}): {openError}");
            return null;
        }

        try
        {
            ChdError streamError = ChdFile.OpenAsStream(chd, ownsChd: false, out ChdImageStream stream);
            if (streamError != ChdError.Chderrnone)
            {
                System.Diagnostics.Debug.WriteLine($"[DiscSerialReader] .chdをストリームとして開けませんでした({filePath}): {streamError}");
                return null;
            }

            using (stream)
            {
                // CHDのCDは1フレームが2448byte(2352byte+サブコード96byte)で並んでいる。
                SectorReader? reader = null;
                ChdTrackInfo? track = chd.Tracks?.FirstOrDefault(t => t.TrackType is ChdTrackType.Mode1Raw or ChdTrackType.Mode2Raw)
                    ?? chd.Tracks?.FirstOrDefault();
                if (track is not null && chd.UnitBytes > 0)
                {
                    reader = SectorReader.ForChd(stream, (long)chd.UnitBytes, (long)track.StartFrame, track.TrackType);
                }

                return ReadSerialFromStream(method, stream, string.Empty, reader);
            }
        }
        finally
        {
            chd.Dispose();
        }
    }

    /// <summary>コンソール別の読み取り処理への分岐。</summary>
    private static string? ReadSerialFromStream(DiscSerialMethod method, Stream stream, string extension, SectorReader? presetReader = null)
    {
        if (method is DiscSerialMethod.GameCube or DiscSerialMethod.Wii)
        {
            // GameCube/WiiはCD-ROMではなくDVDベースで、ここまでのPS1系のようなセクタ構造(2352/2048byteセクタ、ISO9660)を経由しない。
            long headerOffset = FindContainerDiscHeaderOffset(stream, extension);
            if (headerOffset > 0)
            {
                return ReadGameIdAt(stream, headerOffset);
            }

            return method == DiscSerialMethod.GameCube ? ReadGameCubeSerial(stream) : ReadWiiSerial(stream);
        }

        if (string.Equals(extension, ".pbp", StringComparison.OrdinalIgnoreCase))
        {
            // PBPはPSP用EBOOT形式。
            return method is DiscSerialMethod.PlayStation or DiscSerialMethod.PlayStationPortable
                ? ReadSerialFromPbp(stream)
                : null;
        }

        SectorReader? reader = presetReader ?? SectorReader.Detect(stream);
        if (reader is null)
        {
            return null;
        }

        return method switch
        {
            DiscSerialMethod.PlayStation => ReadPlayStationSerial(reader, "BOOT"),
            DiscSerialMethod.PlayStation2 => ReadPlayStationSerial(reader, "BOOT2", "BOOT"),
            DiscSerialMethod.PlayStationPortable => ReadPspSerial(reader),
            DiscSerialMethod.SegaSaturn => ReadSaturnSerial(reader),
            DiscSerialMethod.SegaCd => ReadSegaCdSerial(reader),
            _ => null,
        };
    }

    // コンソール別の読み取り処理

    /// <summary>PS1/PS2: ルートのSYSTEM.CNFを読み、指定キー(BOOT/BOOT2)の行の実行ファイル名からシリアルを組み立てる(例: "BOOT = cdrom:\SLUS_009.08;1" → "SLUS-00908")。</summary>
    private static string? ReadPlayStationSerial(SectorReader reader, params string[] bootKeys)
    {
        byte[]? cnf = Iso9660.ReadRootFile(reader, "SYSTEM.CNF");
        if (cnf is null)
        {
            return null;
        }

        string[] lines = Encoding.ASCII.GetString(cnf).Split('\n');
        foreach (string bootKey in bootKeys)
        {
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq < 0 || !string.Equals(line[..eq].Trim(), bootKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? serial = ParseBootExecutableSerial(line[(eq + 1)..]);
                if (serial is not null)
                {
                    return serial;
                }
            }
        }

        return null;
    }

    /// <summary>"cdrom:\SLUS_009.08;1" のような値から "SLUS-00908" を取り出す。</summary>
    private static string? ParseBootExecutableSerial(string value)
    {
        string fileName = value.Trim();

        int separator = fileName.LastIndexOfAny(new[] { '\\', '/', ':' });
        if (separator >= 0)
        {
            fileName = fileName[(separator + 1)..];
        }

        int semicolon = fileName.IndexOf(';');
        if (semicolon >= 0)
        {
            fileName = fileName[..semicolon];
        }

        Match match = BootExecutablePattern.Match(fileName.Trim());
        if (!match.Success)
        {
            return null;
        }

        return $"{match.Groups[1].Value.ToUpperInvariant()}-{match.Groups[2].Value}{match.Groups[3].Value}";
    }

    /// <summary>PSP: ルートのUMD_DATA.BIN("ULUS-10041|..."形式)の先頭から取り出す。</summary>
    private static string? ReadPspSerial(SectorReader reader)
    {
        byte[]? data = Iso9660.ReadRootFile(reader, "UMD_DATA.BIN");
        if (data is null)
        {
            return null;
        }

        string text = Encoding.ASCII.GetString(data);
        int bar = text.IndexOf('|');
        return FormatCompactSerial(bar >= 0 ? text[..bar] : text);
    }

    /// <summary>セガサターン: 先頭セクタ(IP.BIN)の0x20から10byteの製品番号(例: "MK-81086")。</summary>
    private static string? ReadSaturnSerial(SectorReader reader)
    {
        byte[]? sector = reader.ReadSector(0);
        if (sector is null || !Encoding.ASCII.GetString(sector, 0, 15).Equals("SEGA SEGASATURN", StringComparison.Ordinal))
        {
            return null;
        }

        string serial = Encoding.ASCII.GetString(sector, 0x20, 10).Trim('\0', ' ');
        return serial.Length > 0 ? serial : null;
    }

    /// <summary>メガCD: 先頭セクタの0x180から14byte("GM T-xxxxx -00"形式。先頭2文字が種別、末尾"-00"がリビジョン)から、種別とリビジョンを除いた部分を返す。</summary>
    private static string? ReadSegaCdSerial(SectorReader reader)
    {
        byte[]? sector = reader.ReadSector(0);
        if (sector is null || !Encoding.ASCII.GetString(sector, 0, 14).Equals("SEGADISCSYSTEM", StringComparison.Ordinal))
        {
            return null;
        }

        string field = Encoding.ASCII.GetString(sector, 0x180, 14).Trim('\0', ' ');
        string body = field.Length > 3 && field[2] == ' ' ? field[3..] : field;

        int dash = body.LastIndexOf('-');
        if (dash > 0 && body.Length - dash == 3 && char.IsDigit(body[^1]) && char.IsDigit(body[^2]))
        {
            body = body[..dash];
        }

        body = body.Trim();
        return body.Length > 0 ? body : null;
    }

    /// <summary>GameCubeディスクイメージのマジックナンバー(オフセット0x1C、ビッグエンディアン)。</summary>
    private static readonly byte[] GameCubeMagic = { 0xC2, 0x33, 0x9F, 0x3D };

    /// <summary>Wiiディスクイメージのマジックナンバー(オフセット0x18、ビッグエンディアン)。</summary>
    private static readonly byte[] WiiMagic = { 0x5D, 0x1C, 0x9E, 0xA3 };

    /// <summary>GameCube: ディスクイメージ先頭のGame ID(オフセット0x00-0x05、6文字、例: "GALE01")。</summary>
    private static string? ReadGameCubeSerial(Stream stream) => ReadGameCubeOrWiiSerial(stream, GameCubeMagic, 0x1C);

    /// <summary>Wii: GameCubeと全く同じ位置(オフセット0x00-0x05)にGame IDを持つ、共通のディスクヘッダ形式。</summary>
    private static string? ReadWiiSerial(Stream stream) => ReadGameCubeOrWiiSerial(stream, WiiMagic, 0x18);

    /// <summary>.wbfs(WBFS)・.rvz/.wia(Dolphin)内にある、元ディスクのヘッダ(Game ID〜マジック)の位置を返す。</summary>
    private static long FindContainerDiscHeaderOffset(Stream stream, string extension)
    {
        byte[]? head = ReadAt(stream, 0, 16);
        if (head is null)
        {
            return -1;
        }

        if (head[0] == 'W' && head[1] == 'B' && head[2] == 'F' && head[3] == 'S')
        {
            int shift = head[8];
            return shift is >= 9 and <= 15 ? 1L << shift : -1;
        }

        if ((head[0] == 'R' && head[1] == 'V' && head[2] == 'Z' && head[3] == 1)
            || (head[0] == 'W' && head[1] == 'I' && head[2] == 'A' && head[3] == 1))
        {
            return 0x58;
        }

        return -1;
    }

    /// <summary>指定位置のディスクヘッダからGame IDを読む(GameCube/Wiiどちらのマジックでも可)。</summary>
    private static string? ReadGameIdAt(Stream stream, long offset)
    {
        byte[]? header = ReadAt(stream, offset, 0x20);
        if (header is null)
        {
            return null;
        }

        bool isDisc = header.AsSpan(0x18, 4).SequenceEqual(WiiMagic) || header.AsSpan(0x1C, 4).SequenceEqual(GameCubeMagic);
        if (!isDisc)
        {
            return null;
        }

        string gameId = Encoding.ASCII.GetString(header, 0, 6);
        return gameId.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z')) ? gameId : null;
    }

    /// <summary>GameCube/Wii共通: 先頭6byteのGame IDを取り出し、指定オフセットのマジックナンバーで「本当に生のディスクイメージか」を検証してから返す。</summary>
    private static string? ReadGameCubeOrWiiSerial(Stream stream, byte[] magic, int magicOffset)
    {
        byte[]? header = ReadAt(stream, 0, magicOffset + magic.Length);
        if (header is null || !header.AsSpan(magicOffset, magic.Length).SequenceEqual(magic))
        {
            return null;
        }

        string gameId = Encoding.ASCII.GetString(header, 0, 6);
        bool isValid = gameId.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z'));
        return isValid ? gameId : null;
    }

    // ドリームキャスト(GD-ROM)

    /// <summary>IP.BIN先頭16byteのハードウェアID。</summary>
    private static readonly byte[] DreamcastHardwareId = Encoding.ASCII.GetBytes("SEGA SEGAKATANA ");

    /// <summary>拡張子ごとにドリームキャストのシリアル読み取り方法を振り分ける。</summary>
    private static string? ReadDreamcastSerial(string filePath, string extension) => extension switch
    {
        ".gdi" => ReadDreamcastSerialFromGdi(filePath),
        ".chd" => ReadDreamcastSerialFromChd(filePath),
        _ => null,
    };

    /// <summary>.gdiからトラック3(GD-ROMの高密度領域の先頭。ブート情報が必ずここにある。dreamcast.wiki "GDI format"参照)のファイルを特定し、その先頭セクタを読む。</summary>
    private static string? ReadDreamcastSerialFromGdi(string gdiPath)
    {
        string? trackPath = FindGdiTrackFile(gdiPath, trackNumber: 3);
        if (trackPath is null)
        {
            return null;
        }

        using var stream = new FileStream(trackPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        SectorReader? reader = SectorReader.Detect(stream);
        if (reader is null)
        {
            return null;
        }

        byte[]? sector = reader.ReadSector(0);
        return sector is null ? null : ExtractDreamcastSerial(sector);
    }

    /// <summary>.gdiテキストから、指定トラック番号のファイルの絶対パスを返す。</summary>
    private static string? FindGdiTrackFile(string gdiPath, int trackNumber)
    {
        string[] lines = File.ReadAllLines(gdiPath);
        string directory = Path.GetDirectoryName(gdiPath) ?? string.Empty;

        foreach (string rawLine in lines.Skip(1))
        {
            string[] tokens = TokenizeGdiLine(rawLine.Trim());
            if (tokens.Length < 5 || !int.TryParse(tokens[0], out int number) || number != trackNumber)
            {
                continue;
            }

            string fullPath = Path.Combine(directory, tokens[4]);
            return File.Exists(fullPath) ? fullPath : null;
        }

        return null;
    }

    /// <summary>.gdiの1行をスペース区切りでトークン化する(ファイル名部分は引用符に対応)。</summary>
    private static string[] TokenizeGdiLine(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            if (line[i] == '"')
            {
                int end = line.IndexOf('"', i + 1);
                if (end < 0)
                {
                    end = line.Length;
                }

                tokens.Add(line[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ' ')
                {
                    i++;
                }

                tokens.Add(line[start..i]);
            }
        }

        return tokens.ToArray();
    }

    /// <summary>.chdからドリームキャストのシリアルを読む。</summary>
    private static string? ReadDreamcastSerialFromChd(string filePath)
    {
        ChdError openError = ChdFile.Open(filePath, out ChdFile? chd);
        if (openError != ChdError.Chderrnone || chd is null)
        {
            System.Diagnostics.Debug.WriteLine($"[DiscSerialReader] .chdを開けませんでした({filePath}): {openError}");
            return null;
        }

        try
        {
            ChdTrackInfo? track3 = chd.Tracks?.FirstOrDefault(t => t.TrackNumber == 3);
            if (track3 is null)
            {
                return null;
            }

            // MODE1/2352・MODE2/2352(raw)は、ユーザーデータの前にsync+headerが付く(raw sectorの16byte目/24byte目から)。
            int dataOffset = track3.TrackType switch
            {
                ChdTrackType.Mode1Raw => 16,
                ChdTrackType.Mode2Raw => 24,
                _ => 0,
            };

            long byteOffset = (long)track3.StartFrame * chd.UnitBytes + dataOffset;

            ChdError streamError = ChdFile.OpenAsStream(chd, ownsChd: false, out ChdImageStream stream);
            if (streamError != ChdError.Chderrnone)
            {
                return null;
            }

            using (stream)
            {
                byte[]? sector = ReadAt(stream, byteOffset, 2048);
                return sector is null ? null : ExtractDreamcastSerial(sector);
            }
        }
        finally
        {
            chd.Dispose();
        }
    }

    /// <summary>IP.BIN(mc.pp.se/dc/ip0000.bin.html)から製品番号を取り出す。</summary>
    private static string? ExtractDreamcastSerial(byte[] sector)
    {
        if (sector.Length < 0x4A || !sector.AsSpan(0, DreamcastHardwareId.Length).SequenceEqual(DreamcastHardwareId))
        {
            return null;
        }

        string productNumber = Encoding.ASCII.GetString(sector, 0x40, 10).Trim();
        return productNumber.Length > 0 ? productNumber : null;
    }

    /// <summary>PBP(EBOOT)内蔵のPARAM.SFOからDISC_IDを取り出す("SLUS00908" → "SLUS-00908")。</summary>
    private static string? ReadSerialFromPbp(Stream stream)
    {
        byte[]? header = ReadAt(stream, 0, 40);
        if (header is null || header[0] != 0x00 || header[1] != (byte)'P' || header[2] != (byte)'B' || header[3] != (byte)'P')
        {
            return null;
        }

        // ヘッダの8byte目から、PARAM.SFO/ICON0.PNG/...の開始オフセットが順に並ぶ。
        uint sfoOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        uint nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (nextOffset <= sfoOffset || nextOffset - sfoOffset > MaxSystemFileSize)
        {
            return null;
        }

        byte[]? sfo = ReadAt(stream, sfoOffset, (int)(nextOffset - sfoOffset));
        return sfo is null ? null : FormatCompactSerial(ReadSfoString(sfo, "DISC_ID"));
    }

    /// <summary>PARAM.SFO(PSF形式)から、指定キーの文字列値を読む。</summary>
    private static string? ReadSfoString(byte[] sfo, string key)
    {
        if (sfo.Length < 20 || sfo[0] != 0x00 || sfo[1] != (byte)'P' || sfo[2] != (byte)'S' || sfo[3] != (byte)'F')
        {
            return null;
        }

        uint keyTableStart = BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan(8));
        uint dataTableStart = BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan(12));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan(16));

        for (uint i = 0; i < count; i++)
        {
            long indexOffset = 20 + (i * 16L);
            if (indexOffset + 16 > sfo.Length)
            {
                break;
            }

            ushort keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(sfo.AsSpan((int)indexOffset));
            uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan((int)indexOffset + 4));
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan((int)indexOffset + 12));

            long keyStart = keyTableStart + keyOffset;
            if (keyStart >= sfo.Length)
            {
                continue;
            }

            int keyEnd = Array.IndexOf(sfo, (byte)0, (int)keyStart);
            if (keyEnd < 0)
            {
                keyEnd = sfo.Length;
            }

            if (!Encoding.ASCII.GetString(sfo, (int)keyStart, keyEnd - (int)keyStart).Equals(key, StringComparison.Ordinal))
            {
                continue;
            }

            long valueStart = dataTableStart + dataOffset;
            if (valueStart + dataLength > sfo.Length)
            {
                return null;
            }

            return Encoding.ASCII.GetString(sfo, (int)valueStart, (int)dataLength).TrimEnd('\0').Trim();
        }

        return null;
    }

    /// <summary>"SLUS00908"/"SLUS-00908"を"SLUS-00908"に揃える。</summary>
    private static string? FormatCompactSerial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim('\0', ' ', '\r', '\n');
        Match match = CompactSerialPattern.Match(trimmed);
        return match.Success
            ? $"{match.Groups[1].Value.ToUpperInvariant()}-{match.Groups[2].Value}"
            : (trimmed.Length > 0 ? trimmed : null);
    }

    // cue / zip

    /// <summary>cueファイルから、最初のデータトラックのファイル(実在するもの)の絶対パスを返す。</summary>
    private static string? FindDataTrackFile(string cuePath)
    {
        string? name = FindDataTrackFileName(File.ReadAllText(cuePath));
        if (name is null)
        {
            return null;
        }

        string directory = Path.GetDirectoryName(cuePath) ?? string.Empty;
        string fullPath = Path.Combine(directory, name);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        // cueの名前だけ変えて中のFILE行が古いままの場合に備え、cueと同名の.binがあればそれを使う。
        string sameNameBin = Path.Combine(directory, Path.GetFileNameWithoutExtension(cuePath) + ".bin");
        return File.Exists(sameNameBin) ? sameNameBin : null;
    }

    /// <summary>cueテキストから、最初のデータトラック(AUDIO以外のTRACK)を含むFILEのファイル名を返す。</summary>
    private static string? FindDataTrackFileName(string cueText)
    {
        string? currentFile = null;
        foreach (string rawLine in cueText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                currentFile = ParseCueFileName(line);
            }
            else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase)
                && currentFile is not null
                && !line.EndsWith("AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                return currentFile;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCueFileNames(string cueText)
    {
        foreach (string rawLine in cueText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase) && ParseCueFileName(line) is string name)
            {
                yield return name;
            }
        }
    }

    /// <summary>FILE "Game (Track 1).bin" BINARY / FILE game.bin BINARY からファイル名を取り出す。</summary>
    private static string? ParseCueFileName(string line)
    {
        string rest = line[5..].Trim();
        if (rest.StartsWith('"'))
        {
            int end = rest.IndexOf('"', 1);
            return end > 1 ? rest[1..end] : null;
        }

        // 引用符無しの場合、末尾の1語はファイル種別(BINARY等)。
        int space = rest.LastIndexOf(' ');
        return space > 0 ? rest[..space].Trim() : (rest.Length > 0 ? rest : null);
    }

    /// <summary>zip内のデータトラックのエントリを探す。</summary>
    private static ZipArchiveEntry? FindDataEntryInZip(ZipArchive archive)
    {
        ZipArchiveEntry? cueEntry = archive.Entries.FirstOrDefault(
            e => string.Equals(Path.GetExtension(e.FullName), ".cue", StringComparison.OrdinalIgnoreCase));

        if (cueEntry is not null)
        {
            string cueText;
            using (var reader = new StreamReader(cueEntry.Open()))
            {
                cueText = reader.ReadToEnd();
            }

            string? name = FindDataTrackFileName(cueText);
            if (name is not null)
            {
                string cueEntryPath = cueEntry.FullName.Replace('\\', '/');
                int lastSlash = cueEntryPath.LastIndexOf('/');
                string cueDirectory = lastSlash >= 0 ? cueEntryPath[..(lastSlash + 1)] : string.Empty;
                string target = cueDirectory + name.Replace('\\', '/');

                ZipArchiveEntry? match =
                    archive.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), target, StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    return match;
                }
            }
        }

        return archive.Entries
            .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>zipエントリの先頭limit byteだけを展開して、シーク可能なMemoryStreamにする。</summary>
    private static MemoryStream ReadPrefix(ZipArchiveEntry entry, int limit)
    {
        var buffer = new MemoryStream((int)Math.Min(entry.Length, limit));
        using (Stream source = entry.Open())
        {
            byte[] chunk = new byte[81920];
            while (buffer.Length < limit)
            {
                int toRead = (int)Math.Min(chunk.Length, limit - buffer.Length);
                int read = source.Read(chunk, 0, toRead);
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    // 低レベル読み取り

    /// <summary>指定オフセットからlength byteを読む。</summary>
    private static byte[]? ReadAt(Stream stream, long offset, int length)
    {
        if (offset < 0 || offset + length > stream.Length)
        {
            return null;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[length];
        int total = 0;
        while (total < length)
        {
            int read = stream.Read(buffer, total, length - total);
            if (read <= 0)
            {
                return null;
            }

            total += read;
        }

        return buffer;
    }

    /// <summary>論理セクタ(2048byteのユーザーデータ)単位での読み取り。</summary>
    private sealed class SectorReader
    {
        private readonly Stream _stream;
        private readonly long _sectorSize;
        private readonly int _dataOffset;
        private readonly long _baseOffset;

        private SectorReader(Stream stream, long sectorSize, int dataOffset, long baseOffset = 0)
        {
            _stream = stream;
            _sectorSize = sectorSize;
            _dataOffset = dataOffset;
            _baseOffset = baseOffset;
        }

        /// <summary>CHD用。</summary>
        public static SectorReader ForChd(Stream stream, long unitBytes, long startFrame, ChdTrackType trackType)
        {
            int dataOffset = trackType switch
            {
                ChdTrackType.Mode1Raw => 16,
                ChdTrackType.Mode2Raw => 24,
                _ => 0,
            };

            return new SectorReader(stream, unitBytes, dataOffset, startFrame * unitBytes);
        }

        public static SectorReader? Detect(Stream stream)
        {
            byte[]? head = ReadAt(stream, 0, 16);
            if (head is null)
            {
                return null;
            }

            if (head.AsSpan(0, SyncPattern.Length).SequenceEqual(SyncPattern))
            {
                int dataOffset = head[15] switch
                {
                    1 => 16, // MODE1/2352
                    2 => 24, // MODE2/2352(Form1。PS1/PS2はこちら)
                    _ => -1,
                };

                return dataOffset < 0 ? null : new SectorReader(stream, RawSectorSize, dataOffset);
            }

            return new SectorReader(stream, CookedSectorSize, 0);
        }

        public byte[]? ReadSector(long lba) => ReadAt(_stream, _baseOffset + (lba * _sectorSize) + _dataOffset, CookedSectorSize);
    }

    /// <summary>ISO9660のルートディレクトリ直下の小さなファイルを読むための最小実装。</summary>
    private static class Iso9660
    {
        private const int PrimaryVolumeDescriptorLba = 16;
        private const int RootDirectoryRecordOffset = 156;

        public static byte[]? ReadRootFile(SectorReader reader, string fileName)
        {
            byte[]? pvd = reader.ReadSector(PrimaryVolumeDescriptorLba);
            if (pvd is null || pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8))
            {
                return null;
            }

            uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(RootDirectoryRecordOffset + 2));
            uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(RootDirectoryRecordOffset + 10));
            long sectorCount = Math.Min((rootSize + CookedSectorSize - 1) / CookedSectorSize, MaxRootDirectorySectors);

            for (long s = 0; s < sectorCount; s++)
            {
                byte[]? directory = reader.ReadSector(rootLba + s);
                if (directory is null)
                {
                    return null;
                }

                int pos = 0;
                while (pos < CookedSectorSize)
                {
                    int recordLength = directory[pos];
                    if (recordLength == 0 || recordLength < 34 || pos + recordLength > CookedSectorSize)
                    {
                        break; // このセクタの残りは空き。次のセクタへ
                    }

                    int nameLength = directory[pos + 32];
                    bool isDirectory = (directory[pos + 25] & 0x02) != 0;
                    if (!isDirectory && 33 + nameLength <= recordLength)
                    {
                        string name = Encoding.ASCII.GetString(directory, pos + 33, nameLength);
                        int semicolon = name.IndexOf(';');
                        if (semicolon >= 0)
                        {
                            name = name[..semicolon];
                        }

                        if (name.TrimEnd('.').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            uint fileLba = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(pos + 2));
                            uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(pos + 10));
                            return fileSize == 0 || fileSize > MaxSystemFileSize
                                ? null
                                : ReadFileData(reader, fileLba, (int)fileSize);
                        }
                    }

                    pos += recordLength;
                }
            }

            return null;
        }

        private static byte[]? ReadFileData(SectorReader reader, uint lba, int size)
        {
            byte[] result = new byte[size];
            int copied = 0;
            for (long sector = lba; copied < size; sector++)
            {
                byte[]? data = reader.ReadSector(sector);
                if (data is null)
                {
                    return null;
                }

                int count = Math.Min(CookedSectorSize, size - copied);
                Array.Copy(data, 0, result, copied, count);
                copied += count;
            }

            return result;
        }
    }
}