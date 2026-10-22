using EmulatorLauncher.Models;
using EmulatorLauncher.Storage;

namespace EmulatorLauncher.RomLibrary;

/// <summary>
/// WinUI3側(Views/Controls)がROMファイル/フォルダの一覧・パス解決を取得するための窓口。
///
/// これまでViews/Controlsが<see cref="EmulatorLauncher.Storage"/>のRepositoryを直接呼んでおり、
/// 「WinUI3は画面周りだけにする」という方針に反していた。このファサードを挟むことで、
/// Views/ControlsはEmulatorLauncher.Storageへ一切依存しなくなる
/// (Storageの全メソッドをそのまま公開するのではなく、実際にUI側が使っている操作だけに
/// 絞って公開している。メソッド名・シグネチャは呼び出し元の変更を最小限にするため
/// Repository側とほぼ同じにしてある)。
/// </summary>
public static class GameLibraryFacade
{
    /// <summary>指定コンソールのROMファイル一覧を取得する(GameSelectPage用)。</summary>
    public static Task<IReadOnlyList<RomFile>> GetFilesByConsoleIdAsync(string consoleId, bool includeDeleted = false)
        => RomFileRepository.GetByConsoleIdAsync(consoleId, includeDeleted);

    /// <summary>最近遊んだゲームを新しい順に最大limit件取得する(ダークテーマのコンソール選択画面用)。</summary>
    public static Task<IReadOnlyList<RomFile>> GetRecentlyPlayedAsync(int limit)
        => RomFileRepository.GetRecentlyPlayedAsync(limit);

    /// <summary>
    /// No一覧に対応するROMファイルをまとめて取得する
    /// (コレクション表示。GameSelectPage/CollectionGameSelectPage用)。
    /// </summary>
    public static Task<IReadOnlyList<RomFile>> GetFilesByNosAsync(IReadOnlyCollection<int> nos, bool includeDeleted = false)
        => RomFileRepository.GetByNosAsync(nos, includeDeleted);

    /// <summary>各ROMファイルのIconPathを解決して詰める(GameSelectPage/CollectionGameSelectPage用)。</summary>
    public static Task ResolveIconPathsAsync(IEnumerable<RomFile> files)
        => RomFileRepository.ResolveIconPathsAsync(files);

    /// <summary>コンソールIDごとのROM件数を取得する(ConsoleSelectPageのバッジ表示用)。</summary>
    public static Task<Dictionary<string, int>> GetFileCountsByConsoleIdAsync()
        => RomFileRepository.GetCountsByConsoleIdAsync();

    /// <summary>ROMファイル本体の実パスを解決する(GamePlayPageのゲーム起動用)。</summary>
    public static Task<string?> ResolveFullPathAsync(RomFile file)
        => RomFileRepository.ResolveFullPathAsync(file);

    /// <summary>ゲームを遊び終えた結果(最終プレイ日時・加算するプレイ時間[分])を記録する(GamePlayPage用)。</summary>
    public static Task<bool> RecordPlayAsync(int no, DateTimeOffset lastPlayed, int addMinutes)
        => RomFileRepository.RecordPlayAsync(no, lastPlayed, addMinutes);

    /// <summary>ゲーム別のランチャー名を保存する(空文字=コンソールの既定ランチャー。詳細画面の「ランチャー変更」用)。</summary>
    public static Task<bool> UpdateLauncherAsync(int no, string launcher)
        => RomFileRepository.UpdateLauncherAsync(no, launcher);

    /// <summary>
    /// ゲーム別の個別設定(RomFile.Custom)の一部を書き換えて保存する(コントローラ設定など)。
    /// 保存後のCustom(JSON文字列)を返す。該当するゲームが無ければnull。
    /// </summary>
    public static Task<string?> UpdateCustomAsync(int no, Action<System.Text.Json.Nodes.JsonObject> mutate)
        => RomFileRepository.UpdateCustomAsync(no, mutate);

    // ---- 複数枚組のゲーム(.m3u)。実体はRomDiscSetService ----

    /// <summary>このコンソールで「複数枚をまとめる」が使えるか(いまはPS1のみ)。</summary>
    public static bool IsDiscSetSupported(string consoleId) => RomDiscSetService.IsSupported(consoleId);

    /// <summary>このゲームに「複数枚(ディスク)」の操作(まとめる/解除)を出してよいか(コンソールとファイルの種類で決まる)。</summary>
    public static bool CanGroupDiscs(RomFile game) => RomDiscSetService.CanGroup(game);

    /// <summary>起動前に「何枚目から始めるか」を選べるコンソールか(コアがディスクコントロールを持たないコンソールは不可)。</summary>
    public static bool SupportsStartDiscChoice(string consoleId) => RomDiscSetService.SupportsStartDiscChoice(consoleId);

    /// <summary>複数枚のメディアがフロッピーのコンソールか(表示に光ディスクではなくフロッピーのアイコンを使う)。</summary>
    public static bool IsFloppyMedia(string consoleId) => RomDiscSetService.IsFloppyMedia(consoleId);

    /// <summary>「まとめる」で、1枚目の次以降に選べるゲーム(同じフォルダ・同じコンソールで、まだどのセットにも入っていないもの)。</summary>
    public static Task<IReadOnlyList<RomFile>> GetDiscSetCandidatesAsync(RomFile game)
        => RomDiscSetService.GetCandidatesAsync(game);

    /// <summary>1枚目と2枚目以降(選んだ順)をまとめて、.m3uを書き出して登録する。</summary>
    public static Task<DiscSetResult> CreateDiscSetAsync(RomFile first, IReadOnlyList<RomFile> others)
        => RomDiscSetService.CreateAsync(first, others);

    /// <summary>まとめを解除する(.m3uを削除し、メンバーを一覧へ戻す)。</summary>
    public static Task<DiscSetResult> RemoveDiscSetAsync(RomFile set)
        => RomDiscSetService.RemoveAsync(set);

    /// <summary>.m3uに書かれたディスクの表示名を、書かれた順(コアのディスク番号の順)に返す(起動時のディスク選択用)。</summary>
    public static Task<IReadOnlyList<string>> GetDiscNamesAsync(RomFile set)
        => RomDiscSetService.GetDiscNamesAsync(set);

    /// <summary>.m3uに書かれたディスクのうち、実在しないものの一覧(起動前の確認用。すべてあれば空)。</summary>
    public static IReadOnlyList<string> FindMissingDiscs(string m3uPath)
        => RomDiscSetService.FindMissingMembers(m3uPath);

    /// <summary>登録済みのROMフォルダ一覧を取得する(SettingsPageのフォルダ管理パネル用)。</summary>
    public static Task<IReadOnlyList<RomFolder>> GetAllFoldersAsync(bool includeDeleted = false)
        => RomFolderRepository.GetAllAsync(includeDeleted);
}
