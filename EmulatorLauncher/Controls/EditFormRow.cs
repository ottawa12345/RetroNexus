using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Controls;

/// <summary>編集画面(コンソール/コレクション)の1行分の表示用モデル。</summary>
public sealed class EditFormRow : INotifyPropertyChanged
{
    /// <summary>行の識別子(呼び出し側が行の種類を判定するのに使う)。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>表示だけの行(フォーカスできない)。</summary>
    public bool IsInfo { get; set; }

    /// <summary>削除など、ラベルを赤で表示する行。</summary>
    public bool IsDanger { get; set; }

    private string _label = string.Empty;
    public string Label
    {
        get => _label;
        set { if (_label == value) return; _label = value; OnPropertyChanged(); }
    }

    private string _displayValue = string.Empty;
    public string DisplayValue
    {
        get => _displayValue;
        set { if (_displayValue == value) return; _displayValue = value; OnPropertyChanged(); }
    }

    private bool _isPlaceholder;

    /// <summary>値が未設定で、説明用の文言を出している(グレーで表示する)。</summary>
    public bool IsPlaceholder
    {
        get => _isPlaceholder;
        set
        {
            if (_isPlaceholder == value) return;
            _isPlaceholder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueBrush));
        }
    }

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; OnPropertyChanged(); }
    }

    private ImageSource? _iconSource;

    /// <summary>値の左に出すサムネイル(アイコンの行だけ。nullなら出さない)。</summary>
    public ImageSource? IconSource
    {
        get => _iconSource;
        set
        {
            _iconSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IconVisibility));
        }
    }

    public Visibility IconVisibility => _iconSource is null ? Visibility.Collapsed : Visibility.Visible;

    public Brush LabelBrush => IsDanger
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x39, 0x50))
        : LauncherEditRow.ResolveBrush("AppTextPrimaryBrush", Windows.UI.Color.FromArgb(255, 0x2D, 0x2D, 0x2D));

    public Brush ValueBrush => IsPlaceholder
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xA6, 0xA6, 0xA6))
        : LauncherEditRow.ResolveBrush("SwitchSelectedBlueBrush", Windows.UI.Color.FromArgb(255, 0x30, 0x50, 0xEF));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
