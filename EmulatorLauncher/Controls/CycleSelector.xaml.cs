using System.ComponentModel;
using System.Runtime.CompilerServices;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmulatorLauncher.Controls;

/// <summary>左右の矢印ボタンで選択肢を送る値ピッカー(CycleSelector)共通UserControl。</summary>
public sealed partial class CycleSelector : UserControl, INotifyPropertyChanged
{
    // ★ SelectedValue の DependencyProperty（必須）
    public static readonly DependencyProperty SelectedValueProperty =
        DependencyProperty.Register(
            nameof(SelectedValue),
            typeof(string),
            typeof(CycleSelector),
            new PropertyMetadata(null, OnSelectedValueChanged));

    private static void OnSelectedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CycleSelector)d;
        var newValue = e.NewValue as string;

        // CLR プロパティ側へ反映（既存ロジックを使う）
        control.SelectedValue = newValue;
    }

    // ★ 既存フィールド
    private List<CycleChoice> _choices = new();
    private int _selectedIndex = -1;
    private bool _isGamepadFocused;

    public CycleSelector()
    {
        this.InitializeComponent();
    }

    /// <summary>選べる値の一覧。</summary>
    public IReadOnlyList<CycleChoice> Choices
    {
        get => _choices;
        set
        {
            _choices = value is null ? new List<CycleChoice>() : new List<CycleChoice>(value);
            _selectedIndex = _choices.Count > 0 ? 0 : -1;
            UpdateDisplay();
        }
    }

    /// <summary>現在選択中の値(CycleChoice.Value)。</summary>
    public string? SelectedValue
    {
        get => _selectedIndex >= 0 && _selectedIndex < _choices.Count
            ? _choices[_selectedIndex].Value
            : null;

        set
        {
            if (value is null)
                return;

            int index = _choices.FindIndex(c => c.Value == value);
            if (index < 0)
                return;

            SetSelectedIndex(index);

            // DP 側にも反映（TwoWay x:Bind 用）
            SetValue(SelectedValueProperty, value);
        }
    }

    /// <summary>ページ側が「今ゲームパッドでこの項目を操作している」ことを示すためのフラグ。</summary>
    public bool IsGamepadFocused
    {
        get => _isGamepadFocused;
        set
        {
            if (_isGamepadFocused == value)
                return;

            _isGamepadFocused = value;
            OnPropertyChanged();
        }
    }

    /// <summary>選択が変わるたびに発生する。</summary>
    public event EventHandler? SelectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>次の選択肢へ進む。</summary>
    public void MoveNext()
    {
        if (_choices.Count == 0)
            return;

        int next = GamepadListNavigator.Move(_selectedIndex < 0 ? 0 : _selectedIndex, 1, _choices.Count, wrap: true);
        SetSelectedIndex(next);
    }

    /// <summary>前の選択肢へ戻る。</summary>
    public void MovePrevious()
    {
        if (_choices.Count == 0)
            return;

        int previous = GamepadListNavigator.Move(_selectedIndex < 0 ? 0 : _selectedIndex, -1, _choices.Count, wrap: true);
        SetSelectedIndex(previous);
    }

    private void SetSelectedIndex(int index)
    {
        if (index == _selectedIndex)
            return;

        _selectedIndex = index;
        UpdateDisplay();
        OnPropertyChanged(nameof(SelectedValue));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDisplay()
    {
        ValueText.Text = _selectedIndex >= 0 && _selectedIndex < _choices.Count
            ? _choices[_selectedIndex].Label
            : string.Empty;
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => MovePrevious();
    private void NextButton_Click(object sender, RoutedEventArgs e) => MoveNext();
}
