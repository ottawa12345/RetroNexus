using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EmulatorLauncher.Controls;

/// <summary>HotkeyEditOverlayの左一覧の1行(物理ボタン1つ)。</summary>
public sealed class HotkeyButtonRow : INotifyPropertyChanged
{
    public GamepadButton Button { get; set; }
    public string Label { get; set; } = string.Empty;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnChanged(); }
    }

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>「適用」ボタンのフォーカス状態。</summary>
public sealed class HotkeyApplyState : INotifyPropertyChanged
{
    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFocused)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 特殊割り当ての組み合わせ編集画面。使い方はHotkeyEditOverlay.xamlのコメント参照。
/// </summary>
public sealed partial class HotkeyEditOverlay : UserControl
{
    private const double RowPitch = 92;

    public ObservableCollection<HotkeyButtonRow> Rows { get; } = new();
    public HotkeyApplyState ApplyState { get; } = new();

    public bool IsOpen { get; private set; }

    private TaskCompletionSource<GamepadButton[]?>? _tcs;
    private int _focusIndex;
    private bool _onApply;
    private Func<GamepadButton, string> _labelOf = b => b.ToString();

    public HotkeyEditOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>
    /// 画面を開き、適用(選んだボタンの並び。空なら「使わない」)か、キャンセル(null)になるまで待つ。
    /// </summary>
    /// <param name="title">画面上部の項目名。</param>
    /// <param name="buttons">一覧に並べる物理ボタン。</param>
    /// <param name="labelOf">ボタンの表示名(PSなら記号など)。</param>
    /// <param name="current">いま選ばれているボタン。</param>
    /// <param name="help">右側の説明文。</param>
    public Task<GamepadButton[]?> ShowAsync(string title, IEnumerable<GamepadButton> buttons,
        Func<GamepadButton, string> labelOf, IReadOnlyList<GamepadButton> current, string help)
    {
        TitleText.Text = title;
        HelpText.Text = help;
        _labelOf = labelOf;

        Rows.Clear();
        foreach (GamepadButton b in buttons)
        {
            Rows.Add(new HotkeyButtonRow { Button = b, Label = labelOf(b), IsSelected = current.Contains(b) });
        }

        _initialSelection = string.Join("|", SelectedButtons());
        _focusIndex = Math.Max(Rows.ToList().FindIndex(r => r.IsSelected), 0);
        _onApply = false;
        UpdateFocus();
        RefreshComboText();
        RowsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        Visibility = Visibility.Visible;
        IsOpen = true;
        RowsScrollViewer.UpdateLayout();
        ScrollFocusedIntoView();
        DispatcherQueue.TryEnqueue(() =>
        {
            RowsScrollViewer.UpdateLayout();
            ScrollFocusedIntoView();
        });

        _tcs = new TaskCompletionSource<GamepadButton[]?>();
        return _tcs.Task;
    }

    private void Close(GamepadButton[]? result)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        TaskCompletionSource<GamepadButton[]?>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    private string _initialSelection = string.Empty;

    private GamepadButton[] SelectedButtons() => Rows.Where(r => r.IsSelected).Select(r => r.Button).ToArray();

    /// <summary>変更があるまま戻ろうとしたとき、捨ててよいか確認する(不意に戻って変更が消えないようにする)。</summary>
    private bool IsDirty() => string.Join("|", SelectedButtons()) != _initialSelection;

    private async Task ConfirmDiscardAsync()
    {
        bool discard = await ConfirmOverlay.ShowAsync("保存していない変更があります。変更を捨てて戻りますか?", "変更を捨てる", "編集を続ける");
        if (discard && IsOpen) Close(null);
    }

    private void Apply() => Close(SelectedButtons());

    public void HandleGamepadInput(GamepadButton button)
    {
        if (ConfirmOverlay.IsOpen) { ConfirmOverlay.HandleGamepadInput(button); return; }
        switch (button)
        {
            case GamepadButton.B:
                if (IsDirty()) _ = ConfirmDiscardAsync();
                else Close(null);
                break;
            case GamepadButton.DPadUp:
                if (_onApply) SetApplyFocus(false);
                else Move(-1);
                break;
            case GamepadButton.DPadDown:
                if (!_onApply) Move(1);
                break;
            case GamepadButton.DPadRight:
                if (!_onApply) SetApplyFocus(true);
                break;
            case GamepadButton.DPadLeft:
                if (_onApply) SetApplyFocus(false);
                break;
            case GamepadButton.A:
                if (_onApply) Apply();
                else Toggle(_focusIndex);
                break;
            case GamepadButton.X:
                foreach (HotkeyButtonRow r in Rows) r.IsSelected = false;
                RefreshComboText();
                break;
        }
    }

    private void Move(int delta)
    {
        if (Rows.Count == 0) return;
        _focusIndex = GamepadListNavigator.Move(Math.Clamp(_focusIndex, 0, Rows.Count - 1), delta, Rows.Count, wrap: true);
        UpdateFocus();
        ScrollFocusedIntoView();
    }

    private void SetApplyFocus(bool onApply)
    {
        _onApply = onApply;
        UpdateFocus();
    }

    private void Toggle(int index)
    {
        if (index < 0 || index >= Rows.Count) return;
        Rows[index].IsSelected = !Rows[index].IsSelected;
        RefreshComboText();
    }

    private void RefreshComboText()
    {
        GamepadButton[] sel = SelectedButtons();
        ComboText.Text = sel.Length == 0 ? "なし(この操作は使わない)" : string.Join(" + ", sel.Select(_labelOf));
    }

    private void UpdateFocus()
    {
        for (int i = 0; i < Rows.Count; i++) Rows[i].IsFocused = !_onApply && i == _focusIndex;
        ApplyState.IsFocused = _onApply;
    }

    private void ScrollFocusedIntoView()
    {
        double top = _focusIndex * RowPitch;
        double viewport = RowsScrollViewer.ViewportHeight;
        double offset = RowsScrollViewer.VerticalOffset;
        if (viewport <= 0) return;
        if (top < offset) RowsScrollViewer.ChangeView(null, top, null, disableAnimation: false);
        else if (top + RowPitch > offset + viewport)
            RowsScrollViewer.ChangeView(null, top + RowPitch - viewport, null, disableAnimation: false);
    }

    private void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HotkeyButtonRow row }) return;
        int index = Rows.IndexOf(row);
        if (index < 0) return;
        _onApply = false;
        _focusIndex = index;
        UpdateFocus();
        Toggle(index);
    }

    private void ApplyButton_Tapped(object sender, TappedRoutedEventArgs e) => Apply();
}
