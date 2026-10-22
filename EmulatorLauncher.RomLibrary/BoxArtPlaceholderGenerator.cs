using SD = System.Drawing;
using SDImaging = System.Drawing.Imaging;
using SDText = System.Drawing.Text;

namespace EmulatorLauncher.RomLibrary;

/// <summary>ダミー(プレースホルダー)画像を作ることになった理由。</summary>
public enum BoxArtPlaceholderReason
{
    /// <summary>libretro-thumbnailsの実ファイル一覧(ImageList)に、どの候補ファイル名も存在しなかった。</summary>
    NotInImageList,

    /// <summary>一覧には存在したが、ダウンロードに失敗した(ネットワークエラー等)。</summary>
    DownloadFailed,

    /// <summary>一覧が取得できず、URLを直接組み立てて試したがどれも取得できなかった(404かネットワークエラーかは区別できない)。</summary>
    FetchFailedWithoutList,
}

/// <summary>ボックスアートが取得できなかったROM用に、「グレー背景+テキスト」のダミーPNGを生成する。</summary>
public static class BoxArtPlaceholderGenerator
{
    /// <summary>生成する画像の一辺(px)。</summary>
    private const int ImageSize = 512;

    private const int Padding = 28;

    /// <summary>日本語タイトルも描けるよう、Windows標準の游ゴシックを使う(無ければGDI+が既定フォントへ代替する)。</summary>
    private const string FontName = "Yu Gothic UI";

    private static readonly SD.Color BackgroundColor = SD.Color.FromArgb(0x6E, 0x6E, 0x6E);
    private static readonly SD.Color BorderColor = SD.Color.FromArgb(0x9A, 0x9A, 0x9A);
    private static readonly SD.Color PrimaryTextColor = SD.Color.White;
    private static readonly SD.Color SecondaryTextColor = SD.Color.FromArgb(0xE2, 0xE2, 0xE2);
    private static readonly SD.Color DividerColor = SD.Color.FromArgb(0x8C, 0x8C, 0x8C);

    /// <summary>ダミーPNGをdestinationPathへ生成する(既存ファイルがあれば上書き)。</summary>
    public static bool TryCreate(
        string destinationPath, string title, string localFileName,
        IReadOnlyList<string> remoteFileNames, BoxArtPlaceholderReason reason)
    {
        try
        {
            string? folder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            byte[] png = Render(title, localFileName, remoteFileNames, reason);
            File.WriteAllBytes(destinationPath, png);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BoxArtPlaceholderGenerator] ダミー画像の生成に失敗しました({destinationPath}): {ex.Message}");
            return false;
        }
    }

    private static string GetReasonText(BoxArtPlaceholderReason reason) => reason switch
    {
        BoxArtPlaceholderReason.NotInImageList => "libretro-thumbnails に該当する画像がありません",
        BoxArtPlaceholderReason.DownloadFailed => "画像のダウンロードに失敗しました",
        _ => "画像を取得できませんでした",
    };

    /// <summary>グレー背景に、理由・タイトル・ファイル名を描いたPNGのバイト列を返す。</summary>
    private static byte[] Render(
        string title, string localFileName, IReadOnlyList<string> remoteFileNames, BoxArtPlaceholderReason reason)
    {
        using var bitmap = new SD.Bitmap(ImageSize, ImageSize, SDImaging.PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = SDText.TextRenderingHint.AntiAliasGridFit;
            g.Clear(BackgroundColor);

            // ダミーだと一目で分かるよう、内側に細い枠線を引く。
            using (var borderPen = new SD.Pen(BorderColor, 3f))
            {
                g.DrawRectangle(borderPen, 10, 10, ImageSize - 21, ImageSize - 21);
            }

            float contentWidth = ImageSize - Padding * 2;

            // 上段: NO IMAGE + 理由
            var headerRect = new SD.RectangleF(Padding, Padding, contentWidth, 40);
            using (var headerFont = new SD.Font(FontName, 30f, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel))
            using (var brush = new SD.SolidBrush(PrimaryTextColor))
            using (var format = CreateFormat(SD.StringAlignment.Center, SD.StringAlignment.Center))
            {
                g.DrawString("NO IMAGE", headerFont, brush, headerRect, format);
            }

            var reasonRect = new SD.RectangleF(Padding, headerRect.Bottom + 4, contentWidth, 26);
            DrawFittedText(g, GetReasonText(reason), reasonRect, 17f, 11f, SD.FontStyle.Regular,
                SecondaryTextColor, SD.StringAlignment.Center, SD.StringAlignment.Center);

            float dividerTop = reasonRect.Bottom + 12;
            using (var dividerPen = new SD.Pen(DividerColor, 2f))
            {
                g.DrawLine(dividerPen, Padding, dividerTop, ImageSize - Padding, dividerTop);
            }

            // 中段: タイトル(領域に収まるまでフォントを縮める)
            const float bottomBlockHeight = 150f;
            float bottomTop = ImageSize - Padding - bottomBlockHeight;
            var titleRect = new SD.RectangleF(Padding, dividerTop + 12, contentWidth, bottomTop - 12 - (dividerTop + 12));
            DrawFittedText(g, title, titleRect, 44f, 16f, SD.FontStyle.Bold,
                PrimaryTextColor, SD.StringAlignment.Center, SD.StringAlignment.Center);

            using (var dividerPen = new SD.Pen(DividerColor, 2f))
            {
                g.DrawLine(dividerPen, Padding, bottomTop, ImageSize - Padding, bottomTop);
            }

            // 下段: 差し替え用ファイル名と、探したファイル名
            var saveLabelRect = new SD.RectangleF(Padding, bottomTop + 8, contentWidth, 20);
            DrawFittedText(g, "差し替えるファイル名", saveLabelRect, 14f, 10f, SD.FontStyle.Regular,
                SecondaryTextColor, SD.StringAlignment.Near, SD.StringAlignment.Center);

            var saveNameRect = new SD.RectangleF(Padding, saveLabelRect.Bottom, contentWidth, 32);
            DrawFittedText(g, localFileName, saveNameRect, 26f, 14f, SD.FontStyle.Bold,
                PrimaryTextColor, SD.StringAlignment.Near, SD.StringAlignment.Center);

            var remoteLabelRect = new SD.RectangleF(Padding, saveNameRect.Bottom + 6, contentWidth, 20);
            DrawFittedText(g, "探したファイル名", remoteLabelRect, 14f, 10f, SD.FontStyle.Regular,
                SecondaryTextColor, SD.StringAlignment.Near, SD.StringAlignment.Center);

            var remoteNamesRect = new SD.RectangleF(
                Padding, remoteLabelRect.Bottom, contentWidth, ImageSize - Padding - remoteLabelRect.Bottom);
            DrawFittedText(g, string.Join("\n", remoteFileNames), remoteNamesRect, 15f, 9f, SD.FontStyle.Regular,
                PrimaryTextColor, SD.StringAlignment.Near, SD.StringAlignment.Near);
        }

        using var memory = new MemoryStream();
        bitmap.Save(memory, SDImaging.ImageFormat.Png);
        return memory.ToArray();
    }

    /// <summary>rectに収まる最大のフォントサイズ(maxSize〜minSize)を探して描画する。</summary>
    private static void DrawFittedText(
        SD.Graphics g, string text, SD.RectangleF rect, float maxSize, float minSize, SD.FontStyle style,
        SD.Color color, SD.StringAlignment alignment, SD.StringAlignment lineAlignment)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        using SD.StringFormat format = CreateFormat(alignment, lineAlignment);
        using var brush = new SD.SolidBrush(color);

        for (float size = maxSize; ; size -= 1f)
        {
            bool isLast = size <= minSize;
            using var font = new SD.Font(FontName, Math.Max(size, minSize), style, SD.GraphicsUnit.Pixel);

            SD.SizeF measured = g.MeasureString(text, font, new SD.SizeF(rect.Width, 100_000f), format);
            if (measured.Height <= rect.Height || isLast)
            {
                g.DrawString(text, font, brush, rect, format);
                return;
            }
        }
    }

    private static SD.StringFormat CreateFormat(SD.StringAlignment alignment, SD.StringAlignment lineAlignment) => new()
    {
        Alignment = alignment,
        LineAlignment = lineAlignment,
        Trimming = SD.StringTrimming.EllipsisCharacter,
    };
}
