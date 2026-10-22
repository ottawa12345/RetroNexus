using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>設定画面の右パネルに並ぶ「項目名+説明文」形式のメニュー1件分(Switchの設定画面の「コントローラーの持ちかた/順番を変える」+その説明文と同じ形)。</summary>
public class SettingsMenuEntryItem : INotifyPropertyChanged
{
    /// <summary>項目を識別するキー(SettingsPage側でどの画面を開くか判別するのに使う)。</summary>
    public string Key { get; set; } = string.Empty;

    private string _title = string.Empty;

    /// <summary>項目名。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            OnPropertyChanged();
        }
    }

    /// <summary>項目名の下に表示する説明文(灰色)。</summary>
    public string Description { get; set; } = string.Empty;

    private bool _isFocused;

    /// <summary>ゲームパッドで現在フォーカスしている項目か(SettingsPage.RefreshRowFocusFlagsが設定)。</summary>
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
