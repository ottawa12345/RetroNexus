using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>ゲーム選択画面の詳細オーバーレイ(Views.GameSelectPage)で、コレクション1件分のトグルボタン(ToggleButton)を表示するための表示用ラッパー。</summary>
public class CollectionChipItem : INotifyPropertyChanged
{
    /// <summary>コレクションを一意に識別するId("favorite"等)。</summary>
    public string CollectionId { get; set; } = string.Empty;

    /// <summary>表示名(「お気に入り」コレクションはハート付きの特別な表示名にする)。</summary>
    public string Name { get; set; } = string.Empty;
    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            OnPropertyChanged();
        }
    }
    private bool _isMember;

    /// <summary>現在選択中のゲームがこのコレクションに含まれているかどうか。</summary>
    public bool IsMember
    {
        get => _isMember;
        set
        {
            if (_isMember == value) return;
            _isMember = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
