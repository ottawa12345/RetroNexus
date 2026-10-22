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

/// <summary>MultiChoiceOverlayの左一覧の1行(選択肢1つ)。</summary>
public sealed class MultiChoiceRow : INotifyPropertyChanged
{
    /// <summary>呼び出し側が結果を受け取るときの識別子。</summary>
    public string Id { get; set; } = string.Empty;
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

/// <summary>
/// 複数選択の全画面。使い方はMultiChoiceOverlay.xamlのコメント参照。
/// HotkeyEditOverlay(特殊割り当て)と同じ操作・見た目で、選択肢が任意の文字列になったもの。
/// </summary>
public sealed partial class MultiChoiceOverlay : UserControl
{
    private const double RowPitch = 92;

    public ObservableCollection<MultiChoiceRow> Rows { get; } = new();
    public HotkeyApplyState ApplyState { get; } = new();

    public bool IsOpen { get; private set; }

    private TaskCompletionSource<string[]?>? _tcs;
    private int _focusIndex;
    private bool _onApply;
    private string _emptyText = "なし";
    private HashSet<string> _locked = new();

    public MultiChoiceOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>
    /// 画面を開き、適用(選んだ項目のId。空なら「なし」)か、キャンセル(null)になるまで待つ。
    /// </summary>
    /// <param name="title">画面上部の見出し。</param>
    /// <param name="rows">一覧に並べる項目(Id, 表示名, 選択済みか)。</param>
    /// <param name="help">右側の説明文。</param>
    /// <param name="summaryHeader">右側の「選んでいる項目」欄の見出し。</param>
    /// <param name="emptyText">何も選んでいないときに右側へ出す文。</param>
    /// <param name="lockedIds">外せない項目のId(常に選ばれた状態で、Aやクリックでも解除できない)。</param>
    public Task<string[]?> ShowAsync(string title, IEnumerable<(string Id, string Label, bool Selected)> rows,
        string help, string summaryHeader, string emptyText, IReadOnlyCollection<string>? lockedIds = null)
    {
        _locked = lockedIds is null ? new HashSet<string>() : new HashSet<string>(lockedIds);
        TitleText.Text = title;
        HelpText.Text = help;
        SummaryHeaderText.Text = summaryHeader;
        _emptyText = emptyText;

        Rows.Clear();
        foreach (var r in rows)
        {
            Rows.Add(new MultiChoiceRow { Id = r.Id, Label = r.Label, IsSelected = r.Selected || _locked.Contains(r.Id) });
        }

        _initialSelection = string.Join("|", SelectedIds());
        _focusIndex = Math.Max(Rows.ToList().FindIndex(r => r.IsSelected), 0);
        _onApply = false;
        UpdateFocus();
        RefreshSummary();
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

        _tcs = new TaskCompletionSource<string[]?>();
        return _tcs.Task;
    }

    private void Close(string[]? result)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        TaskCompletionSource<string[]?>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    private string _initialSelection = string.Empty;

    private string[] SelectedIds() => Rows.Where(r => r.IsSelected).Select(r => r.Id).ToArray();

    /// <summary>変更があるまま戻ろうとしたとき、捨ててよいか確認する(不意に戻って変更が消えないようにする)。</summary>
    private bool IsDirty() => string.Join("|", SelectedIds()) != _initialSelection;

    private async Task ConfirmDiscardAsync()
    {
        bool discard = await ConfirmOverlay.ShowAsync("保存していない変更があります。変更を捨てて戻りますか?", "変更を捨てる", "編集を続ける");
        if (discard && IsOpen) Close(null);
    }

    private void Apply() => Close(SelectedIds());

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
                foreach (MultiChoiceRow r in Rows) r.IsSelected = _locked.Contains(r.Id);
                RefreshSummary();
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
        if (_locked.Contains(Rows[index].Id)) return;   // 外せない項目
        Rows[index].IsSelected = !Rows[index].IsSelected;
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        string[] names = Rows.Where(r => r.IsSelected).Select(r => r.Label).ToArray();
        SummaryText.Text = names.Length == 0 ? _emptyText : string.Join("、", names);
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
        if (sender is not FrameworkElement { DataContext: MultiChoiceRow row }) return;
        int index = Rows.IndexOf(row);
        if (index < 0) return;
        _onApply = false;
        _focusIndex = index;
        UpdateFocus();
        Toggle(index);
    }

    private void ApplyButton_Tapped(object sender, TappedRoutedEventArgs e) => Apply();
}
