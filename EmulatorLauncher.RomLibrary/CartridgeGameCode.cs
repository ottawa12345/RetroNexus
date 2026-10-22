using EmulatorLauncher.Services;

namespace EmulatorLauncher.RomLibrary;

/// <summary>GBA/NDSのROMヘッダ内のゲームコード(4文字)を読む。</summary>
internal static class CartridgeGameCode
{
    public static bool Supports(string consoleId) => GetOffset(consoleId) >= 0;

    private static int GetOffset(string consoleId) => consoleId.ToLowerInvariant() switch
    {
        "gba" => 0xAC,
        "nds" => 0x0C,
        _ => -1,
    };

    /// <summary>ストリーム先頭のヘッダからゲームコードを読む。</summary>
    public static string? TryRead(Stream stream, string consoleId)
    {
        int offset = GetOffset(consoleId);
        if (offset < 0) return null;

        try
        {
            byte[] buffer = new byte[offset + 4];
            stream.Position = 0;
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) return null;
                total += read;
            }

            string code = System.Text.Encoding.ASCII.GetString(buffer, offset, 4);
            return code.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z')) ? code : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CartridgeGameCode] ヘッダを読めませんでした: {ex.Message}");
            return null;
        }
    }

    /// <summary>3DSのROM(.3ds/.cci/.cxi)のNCCHヘッダにある製品コード("CTR-P-XXXX")を読む。</summary>
    public static string? TryReadNintendo3dsProductCode(string filePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] head = new byte[0x200];
            if (stream.Read(head, 0, head.Length) < head.Length) return null;

            long nccOffset;
            if (head[0x100] == 'N' && head[0x101] == 'C' && head[0x102] == 'S' && head[0x103] == 'D')
            {
                nccOffset = (long)BitConverter.ToUInt32(head, 0x120) * 0x200;
            }
            else if (head[0x100] == 'N' && head[0x101] == 'C' && head[0x102] == 'C' && head[0x103] == 'H')
            {
                nccOffset = 0;
            }
            else
            {
                return null;
            }

            byte[] ncch = new byte[0x200];
            stream.Position = nccOffset;
            if (stream.Read(ncch, 0, ncch.Length) < ncch.Length) return null;
            if (!(ncch[0x100] == 'N' && ncch[0x101] == 'C' && ncch[0x102] == 'C' && ncch[0x103] == 'H')) return null;

            string code = System.Text.Encoding.ASCII.GetString(ncch, 0x150, 16).TrimEnd('\0', ' ');
            return code.StartsWith("CTR-", StringComparison.Ordinal) ? code : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CartridgeGameCode] 3DSの製品コードを読めませんでした({filePath}): {ex.Message}");
            return null;
        }
    }

    public static string? TryReadFromFile(string filePath, string consoleId)
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            return TryRead(stream, consoleId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CartridgeGameCode] {filePath}: {ex.Message}");
            return null;
        }
    }

    public static string? TryReadFromArchive(string archivePath, string entryName, string consoleId)
    {
        try
        {
            return ArchiveReader.WithEntryStream(archivePath, entryName, stream => TryReadSeekable(stream, consoleId));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CartridgeGameCode] {archivePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>シーク不可のストリーム(アーカイブ内エントリ)でも、先頭から読み進めて取り出す。</summary>
    private static string? TryReadSeekable(Stream stream, string consoleId)
    {
        int offset = GetOffset(consoleId);
        if (offset < 0) return null;

        byte[] buffer = new byte[offset + 4];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0) return null;
            total += read;
        }

        string code = System.Text.Encoding.ASCII.GetString(buffer, offset, 4);
        return code.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z')) ? code : null;
    }
}
