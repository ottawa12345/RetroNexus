using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>設定画面「アプリ設定」の1行(「地域」「操作ボタンの入れ替え」)。</summary>
public class AppSettingRowItem : INotifyPropertyChanged
{
    /// <summary>行を識別するキー(SettingsPage側で押された行を判別するのに使う)。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>項目名(左)。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>説明文(項目名の下にグレーで表示する。空なら表示しない)。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>説明文があるか(XAMLの表示切り替え用)。</summary>
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    private string _valueText = string.Empty;

    /// <summary>現在値の表示(右。青)。</summary>
    public string ValueText
    {
        get => _valueText;
        set
        {
            if (_valueText == value) return;
            _valueText = value;
            OnPropertyChanged();
        }
    }

    private bool _isEnabled = true;

    /// <summary>設定できる状態か。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RowOpacity));
        }
    }

    /// <summary>行全体の不透明度(グレーアウト表示用)。</summary>
    public double RowOpacity => _isEnabled ? 1.0 : 0.4;

    private bool _isFocused;

    /// <summary>ゲームパッドで現在フォーカスしている行か(SettingsPage.RefreshRowFocusFlagsが設定)。</summary>
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
