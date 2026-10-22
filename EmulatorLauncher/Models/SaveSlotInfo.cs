using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>ポーズ/中断メニューの「ロード」で表示するセーブスロット1件分。</summary>
public class SaveSlotInfo : INotifyPropertyChanged
{
    public int SlotNumber { get; set; }
    public bool HasData { get; set; }
    /// <summary>セーブした日時(ローカル時刻)。</summary>
    public DateTime? SavedAt { get; set; }

    /// <summary>ラベルの下に表示する日時文字列(例: 2026/09/30 21:00)。</summary>
    public string DateText => SavedAt is DateTime d ? d.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : string.Empty;

    /// <summary>日時行を表示するか。</summary>
    public bool HasDate => SavedAt is not null;

    /// <summary>オートセーブ枠か(「ロード」画面の末尾にだけ出る。SlotNumberは0)。</summary>
    public bool IsAutoSave { get; set; }

    public string Label => IsAutoSave ? "オートセーブ" : HasData ? $"セーブデータ {SlotNumber}" : $"スロット {SlotNumber}(空き)";

    /// <summary>サムネイル表示ボックスの幅・高さ(論理px)。</summary>
    public double ThumbnailWidth { get; set; } = 200;
    public double ThumbnailHeight { get; set; } = 150;

    private Microsoft.UI.Xaml.Media.ImageSource? _thumbnail;
    public Microsoft.UI.Xaml.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThumbnail)); }
    }

    public bool HasThumbnail => Thumbnail is not null;

    public string ThumbnailText => HasData ? "SAVED" : "No Data";

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
