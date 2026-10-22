using System.Drawing;
using System.Drawing.Imaging;
using EmulatorLauncher.Services;

namespace EmulatorLauncher.RomLibrary;

/// <summary>ダウンロードしたボックスアートの向きをそろえる。</summary>
internal static class BoxArtOrienter
{
    /// <summary>横長が基本のとき、幅/高さがこれ未満なら「明らかに縦長」とみなす(3:4より縦長)。</summary>
    private const double ClearlyPortrait = 0.75;

    /// <summary>縦長が基本のとき、幅/高さがこれを超えれば「明らかに横長」とみなす(4:3より横長)。</summary>
    private const double ClearlyLandscape = 1.33;

    /// <summary>回転した場合は、その内容を表す文字列(ログ用)を返す。</summary>
    public static string? TryOrient(string path, string? orientation, int? rotation = null)
    {
        if (string.IsNullOrEmpty(orientation)) return null;
        bool wantLandscape = string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase);
        bool wantPortrait = string.Equals(orientation, "portrait", StringComparison.OrdinalIgnoreCase);
        if (!wantLandscape && !wantPortrait) return null;

        string tempPath = path + ".rotating";
        try
        {
            // PNGならヘッダだけで大きさが分かる(デコードしなくて済む)。
            (int Width, int Height)? size = ImageHeaderReader.TryReadPngSize(path);

            byte[] bytes = File.ReadAllBytes(path);
            using var input = new MemoryStream(bytes);
            using Image image = Image.FromStream(input);

            int width = size?.Width ?? image.Width;
            int height = size?.Height ?? image.Height;
            if (width <= 0 || height <= 0) return null;

            double ratio = (double)width / height;
            bool wrongWay = wantLandscape ? ratio < ClearlyPortrait : ratio > ClearlyLandscape;
            if (!wrongWay) return null;

            ImageFormat format = image.RawFormat;
            // 角度の指定が無ければ、横長が基本のときは-90度、縦長が基本のときは+90度。
            int degrees = rotation ?? (wantLandscape ? -90 : 90);
            bool clockwise = degrees > 0;
            image.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);

            if (ImageFormat.Jpeg.Equals(format))
            {
                ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (codec is null) return null;
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
                image.Save(tempPath, codec, parameters);
            }
            else
            {
                image.Save(tempPath, ImageFormat.Png.Equals(format) ? ImageFormat.Png : format);
            }

            File.Move(tempPath, path, overwrite: true);
            return $"{width}x{height}({(wantLandscape ? "縦長" : "横長")})の画像を{(clockwise ? "+90度" : "-90度")}回転して{(wantLandscape ? "横向き" : "縦向き")}にしました";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BoxArtOrienter] 画像の回転に失敗しました({path}): {ex.Message}");
            ScanLog.Warn($"画像を回転できませんでした({Path.GetFileName(path)}): {ex.Message}");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 無視 */ }
            return null;
        }
    }
}
