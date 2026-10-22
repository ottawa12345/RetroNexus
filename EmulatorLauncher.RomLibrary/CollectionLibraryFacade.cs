using EmulatorLauncher.Models;
using EmulatorLauncher.Storage;

namespace EmulatorLauncher.RomLibrary;

/// <summary>WinUI3側(Views/Controls)がユーザー定義コレクション(「お気に入り」含む)を扱うための窓口。</summary>
public static class CollectionLibraryFacade
{
    /// <summary>「お気に入り」コレクションの固定ID。</summary>
    public const string FavoriteCollectionId = RomCollectionRepository.FavoriteCollectionId;

    /// <summary>コレクション一覧を取得する(ConsoleSelectPage/CollectionEditOverlay/GameDetailOverlay用)。</summary>
    public static Task<List<RomCollectionInfo>> GetAllAsync()
        => RomCollectionRepository.GetAllAsync();

    /// <summary>指定ROMファイルが所属しているコレクションID一覧を取得する(GameDetailOverlay用)。</summary>
    public static Task<List<string>> GetCollectionIdsForRomFileAsync(int romFileNo)
        => RomCollectionRepository.GetCollectionIdsForRomFileAsync(romFileNo);

    /// <summary>ROMファイルをコレクションへ追加する(GameDetailOverlay用)。</summary>
    public static Task<bool> AddToCollectionAsync(string collectionId, int romFileNo)
        => RomCollectionRepository.AddToCollectionAsync(collectionId, romFileNo);

    /// <summary>ROMファイルをコレクションから外す(GameDetailOverlay用)。</summary>
    public static Task<bool> RemoveFromCollectionAsync(string collectionId, int romFileNo)
        => RomCollectionRepository.RemoveFromCollectionAsync(collectionId, romFileNo);

    /// <summary>コレクションを新規作成する(CollectionEditOverlay用)。</summary>
    public static Task CreateAsync(string name, string iconPath)
        => RomCollectionRepository.CreateAsync(name, iconPath);

    /// <summary>コレクションの名前・アイコンを更新する(CollectionEditOverlay用)。</summary>
    public static Task UpdateAsync(string collectionId, string name, string iconPath)
        => RomCollectionRepository.UpdateAsync(collectionId, name, iconPath);

    /// <summary>コレクションの並び順を、指定したId順に保存する(設定画面の「コレクション並び替え」用)。</summary>
    public static Task ReorderAsync(IReadOnlyList<string> orderedIds)
        => RomCollectionRepository.ReorderAsync(orderedIds);

    /// <summary>コレクションを削除する(CollectionEditOverlay用)。</summary>
    public static Task<bool> SoftDeleteAsync(string collectionId)
        => RomCollectionRepository.SoftDeleteAsync(collectionId);
}
