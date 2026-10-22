using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>中断メニュー「ディスク入れ替え」の右側に出す、ディスク1枚分の行。</summary>
public class DiskMenuItem : INotifyPropertyChanged
{
    /// <summary>コアのディスク番号(0始まり)。</summary>
    public int Index { get; set; }

    /// <summary>ディスクの名前(例: "ディスク2"、FDSなら"1A")。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>いま挿入されているディスクか。</summary>
    public bool IsCurrent { get; set; }

    public string DisplayText => IsCurrent ? $"{Name}(挿入中)" : Name;

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
