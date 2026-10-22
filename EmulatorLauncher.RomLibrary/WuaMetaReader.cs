using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using ZstdSharp;

namespace EmulatorLauncher.RomLibrary;

/// <summary>Wii Uの.wua(Cemuが使うZArchive形式)から、ゲームのIDを読む。</summary>
internal static class WuaMetaReader
{
    private const int BlockSize = 64 * 1024;
    private const int EntriesPerOffsetRecord = 16;
    private const int OffsetRecordSize = 8 + 2 * EntriesPerOffsetRecord;
    private const int FooterSize = 16 * 6 + 32 + 8 + 4 + 4;
    private const uint Magic = 0x169f52d6;
    private const uint Version1 = 0x61bf3a01;
    private const int MaxMetaSize = 1024 * 1024;

    private static readonly Regex ProductCodePattern =
        new(@"<product_code[^>]*>\s*WUP-[A-Z0-9]-([A-Z0-9]{4})\s*<", RegexOptions.Compiled);

    /// <summary>.wuaのmeta.xmlから4文字のゲームIDを読む。</summary>
    public static string? TryReadGameCode(string wuaPath, out string? error)
    {
        error = null;
        try
        {
            using FileStream stream = new(wuaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string? xml = TryReadMetaXml(stream, out error);
            if (xml is null) return null;
            Match m = ProductCodePattern.Match(xml);
            if (m.Success) return m.Groups[1].Value;
            error = "meta.xmlに製品コード(product_code)がありません";
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string? TryReadMetaXml(FileStream stream, out string? error)
    {
        error = null;
        long fileSize = stream.Length;
        if (fileSize <= FooterSize) { error = "ファイルが小さすぎます"; return null; }

        // フッター(すべてビッグエンディアン)
        byte[] footer = ReadExact(stream, fileSize - FooterSize, FooterSize);
        ulong dataOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(0));
        ulong dataSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(8));
        ulong recOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16));
        ulong recSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(24));
        ulong nameOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(32));
        ulong nameSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(40));
        ulong treeOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48));
        ulong treeSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(56));
        ulong totalSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16 * 6 + 32));
        uint version = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(16 * 6 + 32 + 8));
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(16 * 6 + 32 + 12));
        if (magic != Magic || version != Version1 || totalSize != (ulong)fileSize)
        {
            error = $"ZArchiveのフッターが一致しません(magic={magic:X8} version={version:X8} total={totalSize} 実サイズ={fileSize})";
            return null;
        }

        ulong size = (ulong)fileSize;
        if (dataOffset + dataSize > size || recOffset + recSize > size
            || nameOffset + nameSize > size || treeOffset + treeSize > size
            || recSize == 0 || recSize % OffsetRecordSize != 0 || recSize > 256UL * 1024 * 1024
            || nameSize > 0x7FFFFFFF || treeSize < 16 || treeSize % 16 != 0 || treeSize > 256UL * 1024 * 1024)
        {
            error = "フッターのセクション範囲が不正です";
            return null;
        }

        byte[] records = ReadExact(stream, (long)recOffset, (int)recSize);
        byte[] names = ReadExact(stream, (long)nameOffset, (int)nameSize);
        byte[] tree = ReadExact(stream, (long)treeOffset, (int)treeSize);

        // .wuaは「タイトルごとのフォルダ(例: 0005000010101d00_v208)/meta/meta.xml」の形。
        List<uint> candidates = new();
        ReadOnlySpan<byte> root = tree.AsSpan(0, 16);
        if ((BinaryPrimitives.ReadUInt32BigEndian(root) & 0x80000000) != 0) { error = "ルートがフォルダではありません"; return null; }
        uint rootStart = BinaryPrimitives.ReadUInt32BigEndian(root.Slice(4));
        uint rootCount = BinaryPrimitives.ReadUInt32BigEndian(root.Slice(8));
        int nodeCount = tree.Length / 16;
        if ((ulong)rootStart + rootCount > (ulong)nodeCount) { error = "ルートのフォルダ一覧が不正です"; return null; }
        for (uint i = rootStart; i < rootStart + rootCount; i++)
            if ((BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan((int)i * 16, 4)) & 0x80000000) == 0) candidates.Add(i);
        if (candidates.Count == 0) { error = "ルート直下にフォルダがありません"; return null; }

        string NameOf(uint n) => GetName(names, BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan((int)n * 16, 4)) & 0x7FFFFFFF);
        candidates.Sort((a, b) =>
        {
            int ra = NameOf(a).StartsWith("0005000010", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            int rb = NameOf(b).StartsWith("0005000010", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            return ra != rb ? ra.CompareTo(rb) : a.CompareTo(b);
        });

        foreach (uint dir in candidates)
        {
            uint metaFile = FindFile(tree, names, dir, new[] { "meta", "meta.xml" });
            if (metaFile == uint.MaxValue) continue;

            string? text = ReadFileText(stream, tree, records, (long)dataOffset, dataSize, metaFile, out error);
            if (text is not null) return text;
            return null;
        }

        error = "meta/meta.xml が見つかりません(フォルダ: " + string.Join(", ", candidates.Take(5).Select(NameOf)) + ")";
        return null;
    }

    /// <summary>startのフォルダから、名前を順に辿ってファイルのノード番号を返す。</summary>
    private static uint FindFile(byte[] tree, byte[] names, uint start, string[] parts)
    {
        int nodeCount = tree.Length / 16;
        uint node = start;
        foreach (string part in parts)
        {
            ReadOnlySpan<byte> entry = tree.AsSpan((int)node * 16, 16);
            if ((BinaryPrimitives.ReadUInt32BigEndian(entry) & 0x80000000) != 0) return uint.MaxValue; // ファイルの下は辿れない
            uint first = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(4));
            uint count = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(8));
            if ((ulong)first + count > (ulong)nodeCount) return uint.MaxValue;

            uint found = uint.MaxValue;
            for (uint i = first; i < first + count; i++)
            {
                uint nameRef = BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan((int)i * 16, 4)) & 0x7FFFFFFF;
                if (string.Equals(GetName(names, nameRef), part, StringComparison.OrdinalIgnoreCase)) { found = i; break; }
            }
            if (found == uint.MaxValue) return uint.MaxValue;
            node = found;
        }
        return (BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan((int)node * 16, 4)) & 0x80000000) != 0 ? node : uint.MaxValue;
    }

    private static string? ReadFileText(FileStream stream, byte[] tree, byte[] records,
        long dataOffset, ulong dataSize, uint node, out string? error)
    {
        error = null;
        ReadOnlySpan<byte> file = tree.AsSpan((int)node * 16, 16);
        uint offLow = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(4));
        uint sizeLow = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(8));
        uint high = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(12));
        ulong fileOffset = offLow | ((ulong)(high & 0xFFFF) << 32);
        ulong fileLength = sizeLow | ((ulong)(high >> 16) << 32);
        if (fileLength == 0 || fileLength > MaxMetaSize) { error = $"meta.xmlのサイズが不正です({fileLength})"; return null; }

        byte[] result = new byte[fileLength];
        byte[] block = new byte[BlockSize];
        using Decompressor decompressor = new();
        ulong pos = fileOffset;
        int written = 0;
        while (written < result.Length)
        {
            ulong blockIndex = pos / BlockSize;
            int inBlock = (int)(pos % BlockSize);
            if (!LoadBlock(stream, decompressor, records, dataOffset, dataSize, blockIndex, block))
            {
                error = $"ブロック {blockIndex} を読み込めませんでした";
                return null;
            }
            int step = Math.Min(result.Length - written, BlockSize - inBlock);
            Buffer.BlockCopy(block, inBlock, result, written, step);
            written += step;
            pos += (ulong)step;
        }
        return Encoding.UTF8.GetString(result);
    }

    private static bool LoadBlock(FileStream stream, Decompressor decompressor, byte[] records,
        long dataOffset, ulong dataSize, ulong blockIndex, byte[] block)
    {
        ulong recordIndex = blockIndex / EntriesPerOffsetRecord;
        int sub = (int)(blockIndex % EntriesPerOffsetRecord);
        if ((recordIndex + 1) * OffsetRecordSize > (ulong)records.Length) return false;

        ReadOnlySpan<byte> record = records.AsSpan((int)recordIndex * OffsetRecordSize, OffsetRecordSize);
        ulong offset = BinaryPrimitives.ReadUInt64BigEndian(record);
        for (int i = 0; i < sub; i++)
            offset += (ulong)BinaryPrimitives.ReadUInt16BigEndian(record.Slice(8 + i * 2)) + 1;
        int compressedSize = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(8 + sub * 2)) + 1;
        if (offset + (ulong)compressedSize > dataSize) return false;

        byte[] raw = ReadExact(stream, dataOffset + (long)offset, compressedSize);
        if (compressedSize == BlockSize)
        {
            Buffer.BlockCopy(raw, 0, block, 0, BlockSize); // 圧縮されていないブロック
            return true;
        }
        return decompressor.Unwrap(raw, block) == BlockSize;
    }

    private static string GetName(byte[] names, uint offset)
    {
        if (offset >= names.Length) return string.Empty;
        int length = names[offset] & 0x7F;
        int start = (int)offset + 1;
        if ((names[offset] & 0x80) != 0)
        {
            if (offset + 1 >= names.Length) return string.Empty;
            length |= names[offset + 1] << 7;
            start = (int)offset + 2;
        }
        if (start + length > names.Length) return string.Empty;
        return Encoding.UTF8.GetString(names, start, length);
    }

    private static byte[] ReadExact(FileStream stream, long offset, int length)
    {
        byte[] buffer = new byte[length];
        stream.Position = offset;
        int total = 0;
        while (total < length)
        {
            int read = stream.Read(buffer, total, length - total);
            if (read <= 0) throw new EndOfStreamException();
            total += read;
        }
        return buffer;
    }
}
