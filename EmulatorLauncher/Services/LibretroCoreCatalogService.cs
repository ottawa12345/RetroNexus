using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using EmulatorLauncher.Models;
using EmulatorLauncher.Net;

namespace EmulatorLauncher.Services;

/// <summary>
/// cores/libretro.json(利用可能なlibretroコアの一覧と、実際にインストール済みかどうか)の
/// 読み込み・保存、および未インストールのコアをbuildbot.libretro.comのnightlyビルドから
/// ダウンロードしてcoresフォルダへ展開する処理を行う。
///
/// トップ画面の設定 → コア管理(Views.SettingsPage)がこのカタログを参照し、
/// インストール/アンインストールの操作を行う。
/// </summary>
public static class LibretroCoreCatalogService
{
    // Windows x86_64向けnightlyビルドの配布元。"{core_name}.zip"を付けるとダウンロードできる
    // (例: ".../mupen64plus_next_libretro.dll.zip")。
    private const string NightlyBaseUrl = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string GetCoresFolder() => Path.Combine(AppContext.BaseDirectory, "cores");
    // 読み書きするのは data\libretro.json(UserConfigFiles が、初回に cores\libretro.json をコピーし、以後は新しい項目だけ足す)。
    private static string GetCatalogPath() => UserConfigFiles.CoreCatalogPath;

    /// <summary>
    /// cores/libretro.jsonへの「読み込み→書き換え→書き戻し」を1つの操作として直列化するための
    /// ロック。このクラスの4つの更新系メソッド(RefreshInstalledStatusAsync/
    /// DownloadAndInstallAsync/UninstallAsync/SetOptionValueAsync)はいずれも
    /// 「LoadCatalog()で丸ごと読む→一部だけ書き換える→SaveCatalogAsync()で丸ごと書き戻す」
    /// という同じパターンで、互いの存在を意識していない。これらが同時に走ると、
    /// 後から完了した書き込みが前の書き込みの結果を(古いスナップショットのまま)
    /// 上書きして消してしまう、あるいは同じファイルへの書き込みが重なって
    /// JSONとして壊れてしまう(壊れるとLoadCatalog()が例外を握り潰して空の一覧を返すため、
    /// 「コアが一切インストールされていない」ように見えてしまう)おそれがある。
    /// 例えばコア毎の設定画面で内部解像度を切り替えた直後にコアをインストールする、
    /// といった通常操作でも十分に起こり得るため、更新系メソッドはすべてこのロックの中で
    /// 「読み込み→書き換え→書き戻し」を行う(ネットワークダウンロードやzip展開、
    /// dllファイル自体の削除など、カタログファイルに関係しない時間のかかる処理は
    /// ロックの外で行い、ロックを持つ時間を最小限にする)。
    /// </summary>
    private static readonly SemaphoreSlim CatalogLock = new(1, 1);

    /// <summary>cores/libretro.jsonを読み込む。ファイルが無い/壊れている場合は空の一覧を返す(例外は投げない)。</summary>
    public static List<LibretroCoreInfo> LoadCatalog()
    {
        try
        {
            string path = GetCatalogPath();
            if (!File.Exists(path))
            {
                return new List<LibretroCoreInfo>();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<LibretroCoreInfo>>(json, JsonOptions) ?? new List<LibretroCoreInfo>();
        }
        catch
        {
            return new List<LibretroCoreInfo>();
        }
    }

    /// <summary>cores/libretro.jsonへ一覧を書き戻す。</summary>
    public static async Task SaveCatalogAsync(List<LibretroCoreInfo> cores)
    {
        string path = GetCatalogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? UserConfigFiles.DataFolder);

        string json = JsonSerializer.Serialize(cores, JsonOptions);
        await File.WriteAllTextAsync(GetCatalogPath(), json).ConfigureAwait(false);
    }

    /// <summary>指定コンソールに対応するコア定義を探す(最初の1件)。無ければnull。</summary>
    public static LibretroCoreInfo? FindForConsole(string consoleId)
        => LoadCatalog().FirstOrDefault(c => string.Equals(c.ConsoleId, consoleId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// cores/libretro.jsonのInstalledを、実際にcoresフォルダにCoreNameのdllが存在するかどうかで
    /// 実態に合わせて更新する。手動でdllを追加/削除した場合や、外部要因でファイルが消えた場合でも、
    /// コア管理画面(SettingsPage)を開くたびにこれを呼ぶことで表示と実態がズレないようにする。
    /// 変更があった場合のみlibretro.jsonへ保存する。更新後の一覧をそのまま返す(例外は投げない)。
    /// </summary>
    public static async Task<List<LibretroCoreInfo>> RefreshInstalledStatusAsync()
    {
        await CatalogLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<LibretroCoreInfo> catalog = LoadCatalog();
            string coresFolder = GetCoresFolder();
            bool changed = false;

            foreach (LibretroCoreInfo core in catalog)
            {
                if (string.IsNullOrEmpty(core.CoreName))
                {
                    continue;
                }

                bool existsOnDisk = File.Exists(Path.Combine(coresFolder, core.CoreName));
                if (core.Installed != existsOnDisk)
                {
                    core.Installed = existsOnDisk;
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveCatalogAsync(catalog).ConfigureAwait(false);
            }

            return catalog;
        }
        finally
        {
            CatalogLock.Release();
        }
    }

    /// <summary>
    /// 指定エントリのCoreNameを、buildbot.libretro.comのnightlyビルド(Windows x86_64向け)
    /// からダウンロードし、zipをcoresフォルダへ展開して配置する。成功したらlibretro.json側の
    /// installedをyes(true)へ更新して保存する。失敗した場合はfalseを返す(例外は投げない)。
    /// ダウンロード自体は<see cref="DownloadManager"/>経由で行うため、アプリ全体の
    /// 同時ダウンロード数の上限(最大5)を共有する。
    /// </summary>
    public static async Task<bool> DownloadAndInstallAsync(LibretroCoreInfo core)
    {
        if (string.IsNullOrEmpty(core.CoreName))
        {
            return false;
        }

        string coresFolder = GetCoresFolder();
        string zipUrl = $"{NightlyBaseUrl}{core.CoreName}.zip";
        string zipPath = Path.Combine(coresFolder, $"{core.CoreName}.zip");

        AppLog.Info("Core", $"コアをダウンロードします: {core.CoreName} ← {zipUrl}");
        bool downloaded = await DownloadManager.DownloadFileAsync(zipUrl, zipPath).ConfigureAwait(false);
        if (!downloaded)
        {
            AppLog.Warn("Core", $"コアのダウンロードに失敗しました: {core.CoreName}");
            return false;
        }

        bool extracted;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry? dllEntry = archive.Entries.FirstOrDefault(
                e => string.Equals(e.Name, core.CoreName, StringComparison.OrdinalIgnoreCase));

            if (dllEntry is null)
            {
                AppLog.Warn("Core", $"ダウンロードしたzipに {core.CoreName} が入っていませんでした");
                extracted = false;
            }
            else
            {
                dllEntry.ExtractToFile(Path.Combine(coresFolder, core.CoreName), overwrite: true);
                extracted = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LibretroCoreCatalogService] コアの展開に失敗しました({core.CoreName}): {ex.Message}");
            AppLog.Warn("Core", $"コアの展開に失敗しました: {core.CoreName}", ex);
            extracted = false;
        }
        finally
        {
            // 展開の成否によらず、zip自体は残しておく意味が薄いので削除しておく。
            try { File.Delete(zipPath); } catch { /* 削除に失敗しても致命的ではないので無視する */ }
        }

        if (!extracted)
        {
            return false;
        }

        core.Installed = true;
        AppLog.Info("Core", $"コアをインストールしました: {core.CoreName}");

        EnsureCoreSystemFolder(core.CoreName);
        await EnsureSystemFilesAsync(core).ConfigureAwait(false);

        await CatalogLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<LibretroCoreInfo> catalog = LoadCatalog();
            LibretroCoreInfo? existing = catalog.FirstOrDefault(
                c => string.Equals(c.CoreName, core.CoreName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Installed = true;
            }
            await SaveCatalogAsync(catalog).ConfigureAwait(false);
        }
        finally
        {
            CatalogLock.Release();
        }

        return true;
    }

    /// <summary>
    /// インストール直後に、そのコア専用のsystemフォルダ(system/&lt;コアファイル名&gt;)を作る。
    /// BIOS等をあらかじめ置けるようにするため(一度ゲームを起動しないとフォルダが無い、を避ける)。
    /// EmulationHost.GetCoreSystemDirectoryと同じパスになる。既にあれば何もしない。
    /// アンインストール時にこのフォルダは削除しない(BIOSを残すため)。失敗してもインストールは成功のまま。
    /// </summary>
    private static string GetCoreSystemFolderPath(string coreName)
    {
        string folderName = Path.GetFileNameWithoutExtension(coreName);
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            folderName = folderName.Replace(c, '_');
        }

        return Path.Combine(AppContext.BaseDirectory, "system", folderName);
    }

    /// <summary>
    /// libretro.jsonのsystem_urlが指定されているコアについて、systemデータ(zip)をダウンロードして
    /// system/&lt;コアファイル名&gt;/ へ展開する。
    ///  - ダウンロード先は system/Download/&lt;zipのファイル名&gt;。既にあれば再ダウンロードしない。
    ///    展開後もzipは削除しない。
    ///  - 展開は、system/&lt;コアファイル名&gt;/ が空(ファイルもフォルダも無い)のときだけ行う
    ///    (BIOS等を自分で置いている場合に上書き・混在させないため)。
    ///  - アンインストールでは、このフォルダは消さない。
    /// 失敗してもコアのインストール自体は成功のまま(ログを残すだけ)。
    /// </summary>
    private static async Task EnsureSystemFilesAsync(LibretroCoreInfo core)
    {
        try
        {
            string? url = core.SystemUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                // gc/wii等、同じコアを共用する別エントリに指定があればそれを使う。
                url = LoadCatalog()
                    .FirstOrDefault(c => string.Equals(c.CoreName, core.CoreName, StringComparison.OrdinalIgnoreCase)
                                         && !string.IsNullOrWhiteSpace(c.SystemUrl))?.SystemUrl;
            }
            if (string.IsNullOrWhiteSpace(url)) return;

            string systemDir = GetCoreSystemFolderPath(core.CoreName);
            if (Directory.Exists(systemDir) && Directory.EnumerateFileSystemEntries(systemDir).Any())
            {
                AppLog.Info("Core", $"systemフォルダにファイルがあるため、systemデータは展開しません: {systemDir}");
                return;
            }

            string zipName = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrEmpty(zipName)) return;
            string zipPath = Path.Combine(AppContext.BaseDirectory, "system", "Download", zipName);

            if (!File.Exists(zipPath))
            {
                AppLog.Info("Core", $"systemデータをダウンロードします: {zipName}");
                bool downloaded = await DownloadManager.DownloadFileAsync(url, zipPath).ConfigureAwait(false);
                if (!downloaded)
                {
                    AppLog.Warn("Core", $"systemデータをダウンロードできませんでした: {zipName}");
                    return;
                }
            }

            Directory.CreateDirectory(systemDir);
            var extracted = new List<string>();
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(zipPath);
                string rootFull = Path.GetFullPath(systemDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(Path.Combine(systemDir, entry.FullName));
                    // zip内の「../」等で展開先の外へ出ないようにする。
                    if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination); // フォルダのエントリ
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                    extracted.Add(destination);
                }

                AppLog.Info("Core", $"systemデータを展開しました: {core.CoreName} / {extracted.Count} ファイル → {systemDir}");
            }
            catch (InvalidDataException)
            {
                // zipが壊れている(ダウンロード失敗等)。次回インストール時に取り直せるよう消す。
                try { File.Delete(zipPath); } catch { }
                CleanupPartialExtraction(systemDir, extracted);
            }
            catch
            {
                CleanupPartialExtraction(systemDir, extracted);
                throw;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LibretroCoreCatalogService] systemデータの展開に失敗しました({core.CoreName}): {ex.Message}");
            AppLog.Warn("Core", $"systemデータの展開に失敗しました: {core.CoreName}", ex);
        }
    }

    /// <summary>展開の途中で失敗したとき、そこまでに展開したものを消して「空」に戻す(次回やり直せるように)。</summary>
    private static void CleanupPartialExtraction(string systemDir, List<string> extractedFiles)
    {
        foreach (string file in extractedFiles)
        {
            try { File.Delete(file); } catch { }
        }

        try
        {
            foreach (string dir in Directory.GetDirectories(systemDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        }
        catch { }
    }

    private static void EnsureCoreSystemFolder(string coreName)
    {
        try
        {
            Directory.CreateDirectory(GetCoreSystemFolderPath(coreName));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LibretroCoreCatalogService] systemフォルダの作成に失敗しました({coreName}): {ex.Message}");
        }
    }

    /// <summary>
    /// 指定エントリのコアDLLをcoresフォルダから削除し、libretro.json側のinstalledをno(false)へ
    /// 更新して保存する。ファイルが元々存在しない場合もInstalledをfalseに揃えたうえで成功
    /// (true)として扱う。削除自体に失敗した場合(実行中のゲームがそのコアを使用中でファイルが
    /// ロックされている等)はfalseを返す(例外は投げない)。
    /// </summary>
    public static async Task<bool> UninstallAsync(LibretroCoreInfo core)
    {
        if (string.IsNullOrEmpty(core.CoreName))
        {
            return false;
        }

        string dllPath = Path.Combine(GetCoresFolder(), core.CoreName);
        try
        {
            if (File.Exists(dllPath))
            {
                File.Delete(dllPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LibretroCoreCatalogService] コアの削除に失敗しました({core.CoreName}): {ex.Message}");
            AppLog.Warn("Core", $"コアの削除に失敗しました: {core.CoreName}", ex);
            return false;
        }

        core.Installed = false;

        await CatalogLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<LibretroCoreInfo> catalog = LoadCatalog();
            LibretroCoreInfo? existing = catalog.FirstOrDefault(
                c => string.Equals(c.CoreName, core.CoreName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Installed = false;
            }
            await SaveCatalogAsync(catalog).ConfigureAwait(false);
        }
        finally
        {
            CatalogLock.Release();
        }

        return true;
    }

    /// <summary>
    /// 指定コアの指定オプション(optionKeyがLibretroCoreOptionInfo.Keyと一致するもの)の値を
    /// newValueへ変更し、libretro.jsonへ保存する。コア管理画面のInstalledと同じ考え方で、
    /// 選んだ値をそのままカタログファイルへ書き戻して永続化する(将来、コア起動時に
    /// この値をコアオプションとして渡す処理を追加する土台)。
    /// 該当コア/オプションが見つからない場合はfalseを返す(例外は投げない)。
    /// </summary>
    public static async Task<bool> SetOptionValueAsync(LibretroCoreInfo core, string optionKey, string newValue)
    {
        if (string.IsNullOrEmpty(core.CoreName) || string.IsNullOrEmpty(optionKey))
        {
            return false;
        }

        // 呼び出し元が保持しているインスタンス自体もその場で更新しておく
        // (画面側が直近の値を再読込せずそのまま参照し続けても食い違わないように)。
        LibretroCoreOptionInfo? option = core.Options.FirstOrDefault(
            o => string.Equals(o.Key, optionKey, StringComparison.Ordinal));
        if (option is null)
        {
            return false;
        }
        option.Value = newValue;

        await CatalogLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<LibretroCoreInfo> catalog = LoadCatalog();
            // 同じコアを共用する別コンソール(gc/wii等)のエントリへ書かないよう、コンソールIdも照合する。
            LibretroCoreInfo? existingCore = catalog.FirstOrDefault(
                c => string.Equals(c.CoreName, core.CoreName, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(c.ConsoleId, core.ConsoleId, StringComparison.OrdinalIgnoreCase))
                ?? catalog.FirstOrDefault(
                c => string.Equals(c.CoreName, core.CoreName, StringComparison.OrdinalIgnoreCase));
            LibretroCoreOptionInfo? existingOption = existingCore?.Options.FirstOrDefault(
                o => string.Equals(o.Key, optionKey, StringComparison.Ordinal));
            if (existingOption is not null)
            {
                existingOption.Value = newValue;
            }
            await SaveCatalogAsync(catalog).ConfigureAwait(false);
        }
        finally
        {
            CatalogLock.Release();
        }

        return true;
    }
}
