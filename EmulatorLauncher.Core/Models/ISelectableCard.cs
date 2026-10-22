using System.ComponentModel;

namespace EmulatorLauncher.Models;

/// <summary>カルーセルの1枚のカードとして「選択中(IsSelected)かどうかでハイライト表示を切り替えられる」ことを示すマーカーインターフェース。</summary>
public interface ISelectableCard : INotifyPropertyChanged
{
    bool IsSelected { get; }
}
