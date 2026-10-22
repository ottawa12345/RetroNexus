using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>コア毎の設定画面(Views.SettingsPage)で、選択中コアのオプション1項目を一覧表示するための表示用ラッパー。</summary>
public class LibretroCoreOptionListItem : INotifyPropertyChanged
{
    /// <summary>このオプションが属するコア(保存時にどのコアのoptionsを書き換えるか特定するため)。</summary>
    public LibretroCoreInfo Core { get; set; } = new();

    /// <summary>元になったオプション定義(cores/libretro.jsonの1項目)。</summary>
    public LibretroCoreOptionInfo Option { get; set; } = new();

    /// <summary>画面表示用のラベル(例: "内部解像度")。</summary>
    public string Label => Option.Label;

    /// <summary>選択肢一覧。</summary>
    public List<CycleChoice> Choices => Option.Choices
        .Select(c => new CycleChoice { Value = c.Value, Label = c.Label })
        .ToList();

    private string _selectedValue = string.Empty;

    /// <summary>現在選択中の値(Choices中のいずれかのValueと一致する)。</summary>
    public string SelectedValue
    {
        get => _selectedValue;
        set
        {
            if (_selectedValue == value) return;
            _selectedValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedLabel));
        }
    }

    /// <summary>現在選択中の値の表示名(右端に青文字で表示する)。</summary>
    public string SelectedLabel
        => Option.Choices.FirstOrDefault(c => c.Value == _selectedValue)?.Label ?? _selectedValue;

    private bool _isFocused;

    /// <summary>ゲームパッド操作で現在フォーカスしている行(十字キーで選択中、かつCoreOptionsList領域がアクティブ)かどうか。</summary>
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
