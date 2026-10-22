using System.IO.Compression;
using SharpCompress.Archives;

namespace EmulatorLauncher.Services;

/// <summary>アーカイブ内の1ファイルの情報。</summary>
public sealed record ArchiveEntryInfo(string FullName, long Size, uint Crc);

/// <summary>zip / 7z を同じ形で扱うための薄いラッパー。</summary>
public static class ArchiveReader
{
    public static bool IsSevenZip(string path)
        => string.Equals(Path.GetExtension(path), ".7z", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupported(string path)
        => IsSevenZip(path) || string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>アーカイブ内のファイル(ディレクトリを除く)の一覧。</summary>
    public static IReadOnlyList<ArchiveEntryInfo> ListEntries(string archivePath)
    {
        var result = new List<ArchiveEntryInfo>();
        if (IsSevenZip(archivePath))
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
                result.Add(new ArchiveEntryInfo(entry.Key.Replace('\\', '/'), entry.Size, unchecked((uint)entry.Crc)));
            }
        }
        else
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // ディレクトリ
                result.Add(new ArchiveEntryInfo(entry.FullName, entry.Length, unchecked((uint)entry.Crc32)));
            }
        }
        return result;
    }

    /// <summary>指定エントリを読むストリームを渡してactionを実行する(アーカイブはaction終了後に閉じる)。</summary>
    public static T WithEntryStream<T>(string archivePath, string entryName, Func<Stream, T> action)
    {
        if (IsSevenZip(archivePath))
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
                if (!string.Equals(entry.Key.Replace('\\', '/'), entryName, StringComparison.Ordinal)) continue;
                using Stream stream = entry.OpenEntryStream();
                return action(stream);
            }
        }
        else
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry? entry = archive.GetEntry(entryName);
            if (entry is not null)
            {
                using Stream stream = entry.Open();
                return action(stream);
            }
        }

        throw new FileNotFoundException($"アーカイブ内にエントリがありません: {entryName}", archivePath);
    }

    /// <summary>指定エントリの内容をまるごと読み込む。</summary>
    public static byte[] ReadEntryBytes(string archivePath, string entryName)
        => WithEntryStream(archivePath, entryName, stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        });
}
