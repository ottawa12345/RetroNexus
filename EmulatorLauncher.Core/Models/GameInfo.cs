using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>2枚目画像のゲーム選択グリッド、および3枚目の詳細画面で使う1タイトル分の情報。</summary>
public class GameInfo : INotifyPropertyChanged
{
    public string Id { get; set; } = string.Empty;
    public string ConsoleId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public int ReleaseYear { get; set; }
    public string Players { get; set; } = "1人用";
    public string Description { get; set; } = string.Empty;
    public string AccentColorHex { get; set; } = "#455A64";
    public string IconPath { get; set; } = string.Empty;

    /// <summary>実際のROM/ディスクイメージのファイル名、または絶対パス。</summary>
    public string RomPath { get; set; } = string.Empty;

    /// <summary>ソート用: 最近遊んだ日時、プレイ時間(分)。</summary>
    public DateTimeOffset? LastPlayed { get; set; }
    public int PlayTimeMinutes { get; set; }

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
