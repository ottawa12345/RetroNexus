using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Models;

/// <summary>ゲーム詳細画面に並べる、ステートセーブの画像1枚分。</summary>
public sealed class StateImageItem : INotifyPropertyChanged
{
    /// <summary>再開するステートの指定("suspend" / "auto" / スロット番号)。</summary>
    public string Key { get; set; } = "";

    public ImageSource? Image { get; set; }

    /// <summary>画像の左上に添える呼び名(「中断」「オート」「スロット2」)。</summary>
    public string Label { get; set; } = "";

    /// <summary>画像の下に出す保存日時(例: 10/10 22:30。年が違えば年も付ける)。</summary>
    public string DateText { get; set; } = "";

    /// <summary>画像を描く大きさ(論理px)。</summary>
    public double ImageWidth { get; set; } = 260;
    public double ImageHeight { get; set; } = 146;

    private bool _isFocused;

    /// <summary>ゲームパッドのフォーカスがこの画像にあるか(枠の表示に使う)。</summary>
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFocused)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
        }
    }

    /// <summary>選ばれている(フォーカス中の)画像は少し拡大する。</summary>
    public Vector3 Scale => _isFocused ? new Vector3(1.06f, 1.06f, 1f) : Vector3.One;

    public event PropertyChangedEventHandler? PropertyChanged;
}
