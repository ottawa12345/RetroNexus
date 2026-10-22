using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>巻き戻し画面のフィルムストリップに表示する1コマ分の情報。</summary>
public class RewindFrameInfo : INotifyPropertyChanged
{
    public int Index { get; set; }

    /// <summary>サムネイル表示ボックスの幅・高さ(論理px)。</summary>
    public double ThumbnailWidth { get; set; } = 200;
    public double ThumbnailHeight { get; set; } = 150;

    private Microsoft.UI.Xaml.Media.ImageSource? _thumbnail;
    public Microsoft.UI.Xaml.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

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
