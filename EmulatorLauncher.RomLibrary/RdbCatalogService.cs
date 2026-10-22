using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmulatorLauncher.Models;
using EmulatorLauncher.Net;

namespace EmulatorLauncher.RomLibrary;

/// <summary>libretro-thumbnails側に実在する画像1件分の情報。</summary>
public sealed class ImageListEntry
{
    /// <summary>そのままGETすればファイルの生バイト列が返ってくるURL。</summary>
    public string? DirectUrl { get; init; }

    /// <summary>GitHub Git Blob API("/git/blobs/{sha}")のURL。</summary>
    public string? BlobApiUrl { get; init; }
}

/// <summary>data/rdb/rdblist.json(コンソールごとのRetroArch RDB定義一覧)の読み込みと、対応する.rdbファイルがローカルに無ければダウンロードして保存する処理を行う。</summary>
public static class RdbCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string GetRdbFolder() => Path.Combine(AppContext.BaseDirectory, "data", "rdb");
    private static string GetCatalogPath() => Path.Combine(GetRdbFolder(), "rdblist.json");

    /// <summary>指定コンソールのimage一覧("list")をローカルに保存するファイルパス(例: "data/rdb/n64.imagelist.v3.json")。</summary>
    private static string GetImageListPath(RdbSourceInfo source)
        => Path.Combine(GetRdbFolder(), $"{source.ConsoleId}.imagelist.v3.json");

    /// <summary>コンソールIdごとに、image一覧の取得結果(ファイル名 → の辞書)をキャッシュする。</summary>
    private static readonly ConcurrentDictionary<string, Task<Dictionary<string, ImageListEntry>?>> ImageEntriesCache = new();

    /// <summary>の結果のキャッシュ(コンソールIDごと)。</summary>
    private static readonly ConcurrentDictionary<string, Task<Dictionary<string, List<string>>?>> BaseTitleIndexCache = new();

    /// <summary>の結果のキャッシュ(コンソールIDごと)。</summary>
    private static readonly ConcurrentDictionary<string, Task<Dictionary<string, List<string>>?>> CompactTitleIndexCache = new();

    /// <summary>GitHub Contents APIのフォルダ一覧URL ("https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={sha}")を解析するための正規表現。</summary>
    private static readonly Regex ContentsUrlPattern = new(
        @"^https://api\.github\.com/repos/(?<owner>[^/]+)/(?<repo>[^/]+)/contents/(?<path>[^?]*)\?ref=(?<ref>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Git Trees APIのフォルダ指定形式("https://api.github.com/repos/{owner}/{repo}/git/trees/{ref}:{path}")を解析するための正規表現。</summary>
    private static readonly Regex TreesFolderUrlPattern = new(
        @"^https://api\.github\.com/repos/(?<owner>[^/]+)/(?<repo>[^/]+)/git/trees/(?<ref>[^:/?]+):(?<path>[^?]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>rdblist.jsonを読み込む。</summary>
    public static List<RdbSourceInfo> LoadCatalog()
    {
        try
        {
            string path = GetCatalogPath();
            if (!File.Exists(path))
            {
                return new List<RdbSourceInfo>();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<RdbSourceInfo>>(json, JsonOptions) ?? new List<RdbSourceInfo>();
        }
        catch
        {
            return new List<RdbSourceInfo>();
        }
    }

    /// <summary>指定コンソールに対応するRDB定義を探す。</summary>
    public static RdbSourceInfo? FindForConsole(string consoleId)
        => LoadCatalog().FirstOrDefault(r => string.Equals(r.ConsoleId, consoleId, StringComparison.OrdinalIgnoreCase));

    /// <summary>指定コンソール用のrdbファイルのローカルパスを返す。</summary>
    public static async Task<string?> EnsureRdbDownloadedAsync(string consoleId)
    {
        RdbSourceInfo? source = FindForConsole(consoleId);
        if (source is null || string.IsNullOrEmpty(source.Rdb) || string.IsNullOrEmpty(source.Url))
        {
            return null;
        }

        string localPath = Path.Combine(GetRdbFolder(), source.Rdb);
        bool ok = await DownloadManager.DownloadFileIfMissingAsync(source.Url, localPath).ConfigureAwait(false);
        return ok ? localPath : null;
    }

    private static readonly ConcurrentDictionary<string, Task<string?>> BulkImagesCache = new();

    /// <summary>一括取得した画像のローカルキャッシュのフォルダ(data/rdb/images/{コンソールId})。</summary>
    public static string GetBulkImageFolder(RdbSourceInfo source)
        => Path.Combine(GetRdbFolder(), "images", source.ConsoleId);

    /// <summary>rdblist.jsonで"bulk": trueのコンソール向け。</summary>
    public static Task<string?> EnsureBulkImagesAsync(RdbSourceInfo? source)
    {
        if (source is null || !source.Bulk)
        {
            return Task.FromResult<string?>(null);
        }

        return BulkImagesCache.GetOrAdd(source.ConsoleId, _ => DownloadAllImagesAsync(source));
    }

    private static async Task<string?> DownloadAllImagesAsync(RdbSourceInfo source)
    {
        Dictionary<string, ImageListEntry>? entries = await GetImageEntriesAsync(source).ConfigureAwait(false);
        if (entries is null)
        {
            return null;
        }

        string folder = GetBulkImageFolder(source);
        Directory.CreateDirectory(folder);

        var tasks = new List<Task>();
        foreach ((string fileName, ImageListEntry entry) in entries)
        {
            string path = Path.Combine(folder, fileName);
            if (File.Exists(path) || string.IsNullOrEmpty(entry.DirectUrl))
            {
                continue;
            }

            tasks.Add(DownloadManager.DownloadFileWithResultAsync(entry.DirectUrl, path));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return folder;
    }

    /// <summary>RDBの無いコンソール向け。</summary>
    public static async Task<string?> EnsureDatDownloadedAsync(string consoleId)
    {
        RdbSourceInfo? source = FindForConsole(consoleId);
        if (source is null || string.IsNullOrEmpty(source.Dat))
        {
            return null;
        }

        string localPath = Path.Combine(GetRdbFolder(), $"{source.ConsoleId}.dat");
        bool ok = await DownloadManager.DownloadFileIfMissingAsync(source.Dat, localPath).ConfigureAwait(false);
        return ok ? localPath : null;
    }

    /// <summary>rdbSource.ImageList(rdblist.jsonの"list")が設定されていれば、libretro-thumbnails側に実在する画像ファイルの一覧(ファイル名をキーとしたの辞書。大文字小文字は区別しない)を返す。</summary>
    public static Task<Dictionary<string, ImageListEntry>?> GetImageEntriesAsync(RdbSourceInfo? source)
    {
        if (source is null || string.IsNullOrEmpty(source.ImageList))
        {
            return Task.FromResult<Dictionary<string, ImageListEntry>?>(null);
        }

        return ImageEntriesCache.GetOrAdd(source.ConsoleId, _ => LoadOrDownloadImageEntriesAsync(source));
    }

    /// <summary>完全一致で見つからなかった場合の名寄せ()用に、基本タイトル(丸カッコを除いた部分)ごとにファイル名をまとめた索引を返す。</summary>
    public static Task<Dictionary<string, List<string>>?> GetBaseTitleIndexAsync(RdbSourceInfo? source)
    {
        if (source is null)
        {
            return Task.FromResult<Dictionary<string, List<string>>?>(null);
        }

        return BaseTitleIndexCache.GetOrAdd(source.ConsoleId, _ => BuildBaseTitleIndexAsync(source));
    }

    /// <summary>画像の実ファイル一覧から、簡略キー(空白・記号・括弧内を除いた小文字の英数字)→実ファイル名の一覧を作る。</summary>
    public static Task<Dictionary<string, List<string>>?> GetCompactTitleIndexAsync(RdbSourceInfo? source)
    {
        if (source is null)
        {
            return Task.FromResult<Dictionary<string, List<string>>?>(null);
        }

        return CompactTitleIndexCache.GetOrAdd(source.ConsoleId, _ => BuildCompactTitleIndexAsync(source));
    }

    private static async Task<Dictionary<string, List<string>>?> BuildCompactTitleIndexAsync(RdbSourceInfo source)
    {
        Dictionary<string, ImageListEntry>? entries = await GetImageEntriesAsync(source).ConfigureAwait(false);
        if (entries is null)
        {
            return null;
        }

        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string fileName in entries.Keys)
        {
            string nameWithoutExtension = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName;
            string key = BoxArtTitleMatcher.CompactKey(nameWithoutExtension);
            if (key.Length == 0) continue;

            if (!index.TryGetValue(key, out List<string>? list))
            {
                list = new List<string>();
                index[key] = list;
            }

            list.Add(fileName);
        }

        return index;
    }

    private static async Task<Dictionary<string, List<string>>?> BuildBaseTitleIndexAsync(RdbSourceInfo source)
    {
        Dictionary<string, ImageListEntry>? entries = await GetImageEntriesAsync(source).ConfigureAwait(false);
        if (entries is null)
        {
            return null;
        }

        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string fileName in entries.Keys)
        {
            string nameWithoutExtension = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^4]
                    : fileName;
            string baseTitle = BoxArtTitleMatcher.NormalizeBaseTitle(nameWithoutExtension);

            if (!index.TryGetValue(baseTitle, out List<string>? list))
            {
                list = new List<string>();
                index[baseTitle] = list;
            }

            list.Add(fileName);
        }

        return index;
    }

    private static async Task<Dictionary<string, ImageListEntry>?> LoadOrDownloadImageEntriesAsync(RdbSourceInfo source)
    {
        TryResolveImageListUrl(source.ImageList, out string downloadUrl, out string? folderPath);

        string localPath = GetImageListPath(source);
        bool ok = await DownloadManager.DownloadFileIfMissingAsync(downloadUrl, localPath).ConfigureAwait(false);
        if (!ok)
        {
            // GitHubの一覧が取れない(リポジトリ名が違う・API制限等)ときは、ミラー(thumbnails.libretro.com)のフォルダ一覧ページ(HTML)から実ファイル名を集める。
            return await TryLoadImageEntriesFromIndexPageAsync(source, folderPath, localPath).ConfigureAwait(false);
        }

        Dictionary<string, ImageListEntry>? entries = ParseImageEntries(localPath, folderPath, source.Image);
        if (entries is null)
        {
            // 解析できない(壊れている、Trees APIの結果が打ち切られている等)一覧を保存したままにすると、以後ずっと不完全な一覧を使い続けてしまうため、キャッシュを削除しておく(次回のスキャンで取り直す)。
            try
            {
                File.Delete(localPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RdbCatalogService] 一覧キャッシュを削除できませんでした({localPath}): {ex.Message}");
            }
        }

        return entries;
    }

    private static readonly Regex IndexPageImageLinkPattern =
        new("href=\"(?<href>[^\"?/][^\"/]*\\.(?:png|jpg))\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>ミラーのフォルダ一覧ページ("{image}/{folder}/"。Apacheの"Index of"形式)から、画像の一覧を作る。</summary>
    private static async Task<Dictionary<string, ImageListEntry>?> TryLoadImageEntriesFromIndexPageAsync(
        RdbSourceInfo source, string? folderPath, string listCachePath)
    {
        if (string.IsNullOrEmpty(source.Image) || string.IsNullOrEmpty(folderPath))
        {
            return null;
        }

        string indexUrl = source.Image.TrimEnd('/') + "/" + folderPath + "/";
        string htmlPath = listCachePath + ".html";
        if (!await DownloadManager.DownloadFileIfMissingAsync(indexUrl, htmlPath).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            string html = File.ReadAllText(htmlPath);
            var entries = new Dictionary<string, ImageListEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in IndexPageImageLinkPattern.Matches(html))
            {
                string href = m.Groups["href"].Value;
                string name = Uri.UnescapeDataString(System.Net.WebUtility.HtmlDecode(href));
                entries[name] = new ImageListEntry { DirectUrl = indexUrl + href };
            }

            if (entries.Count > 0)
            {
                return entries;
            }

            File.Delete(htmlPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RdbCatalogService] 一覧ページを解析できませんでした({htmlPath}): {ex.Message}");
        }

        return null;
    }

    /// <summary>rdblist.jsonの"list"から、実際にダウンロードするURLと、一覧が指しているフォルダ(リポジトリルートからの相対パス。例: "Named_Boxarts")を決める。</summary>
    private static bool TryResolveImageListUrl(string listUrl, out string downloadUrl, out string? folderPath)
    {
        Match trees = TreesFolderUrlPattern.Match(listUrl);
        if (trees.Success)
        {
            downloadUrl = listUrl;
            folderPath = NormalizeFolderPath(trees.Groups["path"].Value);
            return true;
        }

        Match contents = ContentsUrlPattern.Match(listUrl);
        if (contents.Success)
        {
            string owner = contents.Groups["owner"].Value;
            string repo = contents.Groups["repo"].Value;
            string ref_ = contents.Groups["ref"].Value;
            string rawPath = contents.Groups["path"].Value.Trim('/');

            folderPath = NormalizeFolderPath(rawPath);
            downloadUrl = folderPath is null
                ? $"https://api.github.com/repos/{owner}/{repo}/git/trees/{ref_}"
                : $"https://api.github.com/repos/{owner}/{repo}/git/trees/{ref_}:{rawPath}";
            return true;
        }

        downloadUrl = listUrl;
        folderPath = null;
        return false;
    }

    private static string? NormalizeFolderPath(string rawPath)
    {
        string path = Uri.UnescapeDataString(rawPath).Trim('/');
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>実ファイル一覧のJSONファイルを読み込み、ファイル名 → の辞書を組み立てる。</summary>
    private static Dictionary<string, ImageListEntry>? ParseImageEntries(string path, string? folderPath, string? imageBaseUrl)
    {
        try
        {
            string json = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(json);
            var entries = new Dictionary<string, ImageListEntry>(StringComparer.OrdinalIgnoreCase);

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // GitHub Contents API形式。
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("name", out JsonElement nameElement)
                        || nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? name = nameElement.GetString();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    string? downloadUrl = item.TryGetProperty("download_url", out JsonElement downloadUrlElement)
                        && downloadUrlElement.ValueKind == JsonValueKind.String
                        ? downloadUrlElement.GetString()
                        : null;

                    entries[name] = new ImageListEntry { DirectUrl = downloadUrl };
                }

                return entries;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("tree", out JsonElement treeElement)
                && treeElement.ValueKind == JsonValueKind.Array)
            {
                if (document.RootElement.TryGetProperty("truncated", out JsonElement truncatedElement)
                    && truncatedElement.ValueKind == JsonValueKind.True)
                {
                    System.Diagnostics.Debug.WriteLine($"[RdbCatalogService] 画像一覧が打ち切られています(truncated): {path}");
                    return null;
                }

                foreach (JsonElement item in treeElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("type", out JsonElement typeElement)
                        || typeElement.ValueKind != JsonValueKind.String
                        || typeElement.GetString() != "blob")
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("path", out JsonElement pathElement)
                        || pathElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? entryPath = pathElement.GetString();
                    if (string.IsNullOrEmpty(entryPath))
                    {
                        continue;
                    }

                    int lastSlash = entryPath.LastIndexOf('/');
                    string fileName = lastSlash >= 0 ? entryPath[(lastSlash + 1)..] : entryPath;
                    if (string.IsNullOrEmpty(fileName))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(imageBaseUrl))
                    {
                        // rate limitに引っかかるBlob APIを避け、imageBaseUrl側の直リンクを組み立てる。
                        string repositoryPath = folderPath is null ? entryPath : $"{folderPath}/{entryPath}";
                        entries[fileName] = new ImageListEntry { DirectUrl = BuildRawContentUrl(imageBaseUrl, repositoryPath) };
                    }
                    else
                    {
                        // imageBaseUrlが取れない(rdblist.jsonの"image"が未設定)場合のみ、保険としてBlob API URLを使う。
                        string? blobApiUrl = item.TryGetProperty("url", out JsonElement urlElement)
                            && urlElement.ValueKind == JsonValueKind.String
                            ? urlElement.GetString()
                            : null;

                        entries[fileName] = new ImageListEntry { BlobApiUrl = blobApiUrl };
                    }
                }

                return entries;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>rdblist.jsonの"image"(thumbnails.libretro.comの、コンソールフォルダまでのベースURL。末尾に"Named_Boxarts/"は含まない)と、リポジトリルートからの相対パス(例: "Named_Boxarts/007 - Everything or Nothing (Japan).png")を連結して、実際にGETできるURLを組み立てる。</summary>
    private static string BuildRawContentUrl(string imageBaseUrl, string entryPath)
    {
        string encodedPath = entryPath.Replace(" ", "%20");
        return imageBaseUrl.TrimEnd('/') + "/" + encodedPath;
    }
}