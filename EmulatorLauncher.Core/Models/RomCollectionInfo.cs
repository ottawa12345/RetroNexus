using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>コンソール選択画面の「コレクション」タブに表示する、コンソールをまたいだユーザー定義のゲームまとめ(例: "お気に入り"、「マリオコレクション」)。</summary>
public class RomCollectionInfo : INotifyPropertyChanged, ISelectableCard
{
    /// <summary>コレクションを一意に識別するId("favorite"等)。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>表示名(例: "お気に入り"、"マリオコレクション")。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>このコレクションに含まれるRomFile.Noの一覧(Data.RomCollectionRepository.GetAllAsyncがRomCollectionItemテーブルから引いて詰める)。</summary>
    public List<int> GameIds { get; set; } = new();

    /// <summary>カード表示用のアイコン画像パス(ConsoleInfo.IconPathと同様、"ms-appx:///..."等)。</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>カード表示用のプレースホルダー背景色(IconPathが無い/読み込めない場合に使う)。</summary>
    public string AccentColorHex { get; set; } = "#607D8B";
    public bool IsAddNewCard { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
