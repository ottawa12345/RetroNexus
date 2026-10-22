using EmulatorLauncher.Models;
using EmulatorLauncher.Storage;

namespace EmulatorLauncher.RomLibrary;

/// <summary>
/// ゲームのスクリーンショットの保存場所(撮影する側と、詳細画面で見せる側で共通)。
/// 場所: &lt;ROMを登録したフォルダ&gt;/RetroNexus/Named_Snaps/&lt;ボックスアートと同じ名前&gt;/screenshot001.png(以後002, 003...)。
/// ボックスアートが無いゲームは、ROMのファイル名(拡張子なし)をフォルダ名にする。
/// ROMフォルダが見つからない(本体起動など)ときは、アプリのフォルダの screenshots/&lt;名前&gt;/ を使う。
/// </summary>
public static class ScreenshotLocator
{
    private const string FilePrefix = "screenshot";

    /// <summary>そのゲームのスクリーンショットのフォルダ(まだ無いこともある。作らない)。</summary>
    public static async Task<string> GetFolderAsync(RomFile game)
    {
        string name = !string.IsNullOrEmpty(game.IconPath)
            ? Path.GetFileNameWithoutExtension(game.IconPath)
            : Path.GetFileNameWithoutExtension(game.FileName);
        if (string.IsNullOrWhiteSpace(name)) name = game.IsBiosBoot ? "_bios" : "_game";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "_game";

        string baseDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        if (!game.IsBiosBoot)
        {
            RomFolder? folder = await RomFolderRepository.GetAsync(game.ParentId).ConfigureAwait(false);
            if (folder is not null && !string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path))
            {
                baseDir = Path.Combine(folder.Path, "RetroNexus", "Named_Snaps");
            }
        }

        return Path.Combine(baseDir, name);
    }

    /// <summary>次に保存するファイルのパス(screenshot001.png から、既存の最大番号+1)。フォルダは作る。</summary>
    public static string NextFilePath(string folder)
    {
        Directory.CreateDirectory(folder);

        int next = 1;
        foreach ((int number, _) in Enumerate(folder))
        {
            if (number >= next) next = number + 1;
        }

        return Path.Combine(folder, $"{FilePrefix}{next:000}.png");
    }

    /// <summary>新しいもの(番号が大きいもの)から最大count枚のファイルパス。無ければ空。</summary>
    public static async Task<IReadOnlyList<string>> GetLatestAsync(RomFile game, int count)
    {
        try
        {
            string folder = await GetFolderAsync(game).ConfigureAwait(false);
            if (!Directory.Exists(folder)) return Array.Empty<string>();

            return await Task.Run(() => Enumerate(folder)
                .OrderByDescending(e => e.Number)
                .Take(count)
                .Select(e => e.Path)
                .ToList()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<(int Number, string Path)> Enumerate(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, FilePrefix + "*.png"))
        {
            string digits = Path.GetFileNameWithoutExtension(file).Substring(FilePrefix.Length);
            if (int.TryParse(digits, out int n)) yield return (n, file);
        }
    }
}
