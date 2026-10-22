using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>
/// 設定画面(Views.SettingsPage)の左メニュー1件分。
/// Switchの設定画面と同じく、見た目を2つの状態で表す:
///  - <see cref="IsActive"/>: 今右側に内容を表示しているカテゴリ(青い縦バー+青文字)。
///    フォーカスが右側へ移った後も残る。
///  - <see cref="IsFocused"/>: 左メニューを操作中で、かつこの項目を選んでいる状態
///    (<see cref="IsActive"/>に加えて、白背景+水色の枠)。
/// SettingsPage.RefreshCategoryFlagsが設定し直す。
/// </summary>
public class SettingsCategoryItem : INotifyPropertyChanged
{
    public SettingsCategoryItem(string label, bool separatorBelow = false)
    {
        Label = label;
        HasSeparator = separatorBelow;
    }

    public bool HasSeparator { get; }

    /// <summary>メニューに表示するカテゴリ名。</summary>
    public string Label { get; }

    private bool _isActive;

    /// <summary>右側に内容を表示中のカテゴリかどうか(青い縦バー+青文字)。</summary>
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

    private bool _isFocused;

    /// <summary>左メニューを操作中で、この項目にフォーカスがあるかどうか(水色の枠+白背景)。</summary>
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

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
