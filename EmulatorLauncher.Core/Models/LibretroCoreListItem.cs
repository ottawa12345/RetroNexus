using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>コア管理画面(Views.SettingsPage)でlibretroコア1件を一覧表示するための表示用ラッパー。</summary>
public class LibretroCoreListItem : INotifyPropertyChanged
{
    /// <summary>元になったカタログ情報(cores/libretro.jsonの1エントリ)。</summary>
    public LibretroCoreInfo Core { get; set; } = new();

    /// <summary>一覧表示用の名前(例: "Nintendo - Nintendo 64 (Mupen64Plus-Next)")。</summary>
    public string DisplayName => Core.DisplayName;

    /// <summary>対応するコンソールのId("fc"、"n64"等)。</summary>
    public string ConsoleId => Core.ConsoleId;

    /// <summary>一覧表示用のコンソール名。</summary>
    public string ConsoleDisplayName { get; set; } = string.Empty;

    private bool _installed;

    /// <summary>インストール済みかどうか。</summary>
    public bool Installed
    {
        get => _installed;
        set
        {
            if (_installed == value) return;
            _installed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActionButtonText));
        }
    }

    private bool _isBusy;

    /// <summary>ダウンロード(インストール)/削除(アンインストール)処理を実行中かどうか。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionButtonText));
            OnPropertyChanged(nameof(IsActionEnabled));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>操作ボタン(インストール/アンインストール)を押せる状態かどうか。</summary>
    public bool IsActionEnabled => !IsBusy;

    private bool _isFocused;

    /// <summary>ゲームパッド操作で現在フォーカスしている行(十字キーで選択中、かつその一覧の領域がアクティブ)かどうか。</summary>
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

    private bool _isActive;

    /// <summary>詳細設定画面の左の一覧で、今右側にオプションを表示しているコアか(青い縦バー+青文字)。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>一覧に表示する状態文字列。</summary>
    public string StatusText => IsBusy
        ? (Installed ? "アンインストール中…" : "インストール中…")
        : (Installed ? "インストール済み" : "未インストール");

    /// <summary>操作ボタンに表示する文字列。</summary>
    public string ActionButtonText => IsBusy
        ? (Installed ? "アンインストール中…" : "インストール中…")
        : (Installed ? "アンインストール" : "インストール");

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
