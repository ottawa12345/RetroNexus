using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>ゲームフォルダ管理画面(Views.SettingsPage)でRomFolder1件を一覧表示するための表示用ラッパー。</summary>
public class RomFolderListItem : INotifyPropertyChanged
{
    /// <summary>元になったRomFolder(削除処理はこのインスタンスのNoに対して行う)。</summary>
    public RomFolder Folder { get; set; } = new();

    /// <summary>一覧表示用のフォルダパス。</summary>
    public string DisplayPath => Folder.Path;

    /// <summary>一覧表示用のコンソール名。</summary>
    public string ConsoleDisplayName { get; set; } = string.Empty;

    /// <summary>一覧先頭の「＋ 新規追加」カードかどうか。</summary>
    public bool IsAddNewCard { get; set; }

    private bool _isBusy;

    /// <summary>削除処理を実行中かどうか。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActionEnabled));
            OnPropertyChanged(nameof(AddCardLabel));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>「＋ 新規追加」カードの表示文言。</summary>
    public string AddCardLabel => IsBusy ? "フォルダを検索中…" : "＋ 新規追加のフォルダを選ぶ";

    /// <summary>一覧の右端に出す状態表示(削除処理中だけ「削除中…」、それ以外は空)。</summary>
    public string StatusText => IsBusy ? "削除中…" : string.Empty;

    /// <summary>削除ボタンを押せる状態かどうか。</summary>
    public bool IsActionEnabled => !IsBusy;

    private bool _isDeleteConfirming;

    /// <summary>「削除」の1回目のA/クリックを経て、確認表示になっている状態かどうか。</summary>
    public bool IsDeleteConfirming
    {
        get => _isDeleteConfirming;
        set
        {
            if (_isDeleteConfirming == value) return;
            _isDeleteConfirming = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeleteButtonLabel));
        }
    }

    /// <summary>削除ボタンの表示文言(確認待ちの間は誤操作防止のため文言を変える)。</summary>
    public string DeleteButtonLabel => IsDeleteConfirming ? "本当に削除?(もう一度で削除)" : "削除";

    private bool _isFocused;

    /// <summary>ゲームパッド操作で現在フォーカスしている行(十字キーで選択中、かつFolderList領域がアクティブ)かどうか。</summary>
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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
