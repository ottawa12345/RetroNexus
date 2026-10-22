using System.Net;
using System.Net.Http.Headers;
using System.Threading;

namespace EmulatorLauncher.Net;

/// <summary>ダウンロード結果の種別。</summary>
public enum DownloadResult
{
    /// <summary>ダウンロードして保存できた。</summary>
    Success,

    /// <summary>サーバーが404/410を返した(ファイルが実在しないと確定している)。</summary>
    NotFound,

    /// <summary>それ以外の失敗(429/403等のレート制限、5xx、タイムアウト、ネットワーク断、ディスク書き込み失敗等)。</summary>
    Failed,
}

/// <summary>アプリ内のHTTPダウンロード処理を一元管理する窓口。</summary>
public static class DownloadManager
{
    private const int MaxConcurrentDownloads = 5;

    /// <summary>GitHubのGit Blob API(/git/blobs/{sha})に対してこのAcceptヘッダーを指定すると、既定のJSON(content;base64エンコード済み)ではなく、生のバイナリデータをそのまま返してもらえる。</summary>
    public const string GitHubRawBlobAcceptMediaType = "application/vnd.github.raw+json";

    private static readonly SemaphoreSlim ConcurrencyLimiter = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>GitHub APIは"User-Agent"ヘッダーが無いリクエストを一律403 Forbiddenで拒否する(raw.githubusercontent.com側は不要だが、api.github.com/…/contents等のAPIエンドポイントでは必須)。</summary>
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EmulatorLauncher", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>destinationPathに既にファイルがあれば何もせずtrueを返す。</summary>
    public static async Task<bool> DownloadFileIfMissingAsync(string url, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            return true;
        }

        return await DownloadFileAsync(url, destinationPath).ConfigureAwait(false);
    }

    /// <summary>urlからdestinationPathへダウンロードする(既存ファイルがあれば上書き)。</summary>
    public static async Task<bool> DownloadFileAsync(string url, string destinationPath, string? acceptMediaType = null)
        => await DownloadFileWithResultAsync(url, destinationPath, acceptMediaType).ConfigureAwait(false) == DownloadResult.Success;

    /// <summary>と同じだが、失敗の種類()を返す。</summary>
    public static async Task<DownloadResult> DownloadFileWithResultAsync(string url, string destinationPath, string? acceptMediaType = null)
    {
        await ConcurrencyLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            string? folder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            byte[] data;
            if (acceptMediaType is null)
            {
                data = await HttpClient.GetByteArrayAsync(url).ConfigureAwait(false);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptMediaType));

                using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }

            await File.WriteAllBytesAsync(destinationPath, data).ConfigureAwait(false);
            return DownloadResult.Success;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DownloadManager] ファイルが存在しません({ex.StatusCode}: {url})");
            return DownloadResult.NotFound;
        }
        catch (Exception ex)
        {
            // ボックスアート等、取れなくても致命的ではないダウンロードを主眼にしているため、ここで諦めてfalseを返すだけにする(呼び出し側もほぼ発火して待たない使い方をする)。
            System.Diagnostics.Debug.WriteLine(
                $"[DownloadManager] ダウンロードに失敗しました({url} → {destinationPath}): {ex.Message}");
            return DownloadResult.Failed;
        }
        finally
        {
            ConcurrencyLimiter.Release();
        }
    }
}