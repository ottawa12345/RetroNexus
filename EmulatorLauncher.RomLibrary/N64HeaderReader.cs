using System.IO.Compression;
using System.Text;
using EmulatorLauncher.Services;

namespace EmulatorLauncher.RomLibrary;

/// <summary>N64のROM1本から取り出した情報。</summary>
public sealed record N64RomInfo(string GameCode, string InternalTitle, int Version, uint Crc32, string ByteOrder);

/// <summary>N64のROMファイル(.z64/.v64/.n64、およびそれらを含む.zip)から、ゲームコードとCRC-32を取り出す。</summary>
public static class N64HeaderReader
{
    /// <summary>N64のROMとして扱う拡張子(zipは中身を調べる)。</summary>
    public static readonly string[] RomExtensions = { ".z64", ".v64", ".n64" };

    /// <summary>読み込むROMの最大サイズ(これを超えるものは対象外)。</summary>
    private const long MaxRomBytes = 256L * 1024 * 1024;

    /// <summary>ファイル(.z64/.v64/.n64または.zip)から情報を読む。</summary>
    public static N64RomInfo? ReadFile(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!IsRomName(entry.Name) || entry.Length <= 0 || entry.Length > MaxRomBytes) continue;
                using Stream s = entry.Open();
                byte[] data = ReadAll(s, entry.Length);
                N64RomInfo? info = Parse(data);
                if (info is not null) return info;
            }
            return null;
        }

        if (!IsRomName(path)) return null;
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < 0x1000 || fi.Length > MaxRomBytes) return null;
        using FileStream fs = File.OpenRead(path);
        return Parse(ReadAll(fs, fi.Length));
    }

    /// <summary>ROM全体のバイト列から情報を読む。</summary>
    public static N64RomInfo? Parse(byte[] data)
    {
        if (data.Length < 0x1000) return null;

        string order;
        if (data[0] == 0x80 && data[1] == 0x37 && data[2] == 0x12 && data[3] == 0x40) order = "z64";
        else if (data[0] == 0x37 && data[1] == 0x80 && data[2] == 0x40 && data[3] == 0x12) order = "v64";
        else if (data[0] == 0x40 && data[1] == 0x12 && data[2] == 0x37 && data[3] == 0x80) order = "n64";
        else return null;

        if (order == "v64")
        {
            // 2バイトずつ入れ替わっている。
            for (int i = 0; i + 1 < data.Length; i += 2) (data[i], data[i + 1]) = (data[i + 1], data[i]);
        }
        else if (order == "n64")
        {
            // 4バイトが逆順(リトルエンディアン)。
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                (data[i], data[i + 3]) = (data[i + 3], data[i]);
                (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
            }
        }

        string code = ReadCode(data);
        string title = ReadTitle(data);
        int version = data[0x3F];
        uint crc = Crc32.Compute(data);
        return new N64RomInfo(code, title, version, crc, order);
    }

    private static bool IsRomName(string name)
    {
        string ext = Path.GetExtension(name);
        foreach (string e in RomExtensions)
        {
            if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static byte[] ReadAll(Stream s, long length)
    {
        var buffer = new byte[length];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read != buffer.Length) Array.Resize(ref buffer, read);
        return buffer;
    }

    private static string ReadCode(byte[] data)
    {
        var sb = new StringBuilder(4);
        for (int i = 0x3B; i <= 0x3E; i++)
        {
            char c = (char)data[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
            if (!ok) return string.Empty;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ReadTitle(byte[] data)
    {
        var sb = new StringBuilder(20);
        for (int i = 0x20; i < 0x34; i++)
        {
            byte b = data[i];
            if (b == 0) break;
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '?');
        }
        return sb.ToString().Trim();
    }
}
