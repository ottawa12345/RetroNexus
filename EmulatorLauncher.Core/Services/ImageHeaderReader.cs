namespace EmulatorLauncher.Services;

/// <summary>画像ファイルの幅・高さだけを、ファイル全体をデコードせずヘッダから軽量に読み取る。</summary>
public static class ImageHeaderReader
{
    /// <summary>PNGファイルの先頭からIHDRチャンクを読み、幅・高さを取得する。</summary>
    public static (int Width, int Height)? TryReadPngSize(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            // PNGシグネチャ(8byte) + IHDRチャンクの 長さ(4byte)+種別"IHDR"(4byte) + 幅(4byte)+高さ(4byte) = 先頭24byteだけ読めば幅・高さが分かる。
            Span<byte> header = stackalloc byte[24];
            int read = stream.Read(header);
            if (read < 24)
            {
                return null;
            }

            ReadOnlySpan<byte> pngSignature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (!header[..8].SequenceEqual(pngSignature))
            {
                return null;
            }

            // header[8..12]=チャンク長, header[12..16]="IHDR", header[16..20]=幅, header[20..24]=高さ(いずれもPNG仕様によりビッグエンディアン)
            int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

            return width > 0 && height > 0 ? (width, height) : null;
        }
        catch
        {
            return null;
        }
    }
}
