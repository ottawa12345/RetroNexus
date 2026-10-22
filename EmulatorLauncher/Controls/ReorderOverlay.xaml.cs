using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 並び順を入れ替える全画面の画面。使い方・操作はReorderOverlay.xamlのコメント参照。
/// </summary>
public sealed partial class ReorderOverlay : UserControl
{
    public ObservableCollection<ReorderItem> Items { get; } = new();

    public bool IsOpen { get; private set; }

    private int _selectedIndex;

    /// <summary>つかんでいる行(つかんでいなければnull)。</summary>
    private ReorderItem? _grabbed;

    /// <summary>つかんだ時点の位置(Bでキャンセルしたとき戻す)。</summary>
    private int _grabStartIndex;

    private List<string> _originalOrder = new();

    /// <summary>右側の「保存する」ボタンの表示状態(変更があるときだけ有効)。</summary>
    public LauncherSaveButtonState SaveState { get; } = new();

    /// <summary>ゲームパッドのフォーカスが「保存する」ボタンにあるか(falseなら一覧)。</summary>
    private bool _onSave;

    private TaskCompletionSource<IReadOnlyList<string>?>? _tcs;

    public ReorderOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>
    /// 画面を表示し、保存して戻る(B)まで待つ。順番が変わっていれば新しいId順、変わっていなければnullを返す。
    /// </summary>
    public Task<IReadOnlyList<string>?> ShowAsync(string title, IEnumerable<ReorderItem> items)
    {
        TitleText.Text = title;

        Items.Clear();
        foreach (ReorderItem item in items)
        {
            item.IsGrabbed = false;
            Items.Add(item);
        }

        _originalOrder = Items.Select(i => i.Id).ToList();
        _grabbed = null;
        _selectedIndex = 0;
        _onSave = false;
        UpdateFocusVisuals();
        RefreshSaveState();
        ListScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        Visibility = Visibility.Visible;
        IsOpen = true;

        _tcs = new TaskCompletionSource<IReadOnlyList<string>?>();
        return _tcs.Task;
    }

    private bool HasChanges() => !Items.Select(i => i.Id).SequenceEqual(_originalOrder);

    private void Close(bool save)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        IReadOnlyList<string>? result = save && HasChanges() ? Items.Select(i => i.Id).ToList() : null;

        TaskCompletionSource<IReadOnlyList<string>?>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    /// <summary>保存ボタンの有効/無効(変更があり、行をつかんでいないときだけ有効)と、操作ガイドの切り替え。</summary>
    private void RefreshSaveState()
    {
        SaveState.IsEnabled = _grabbed is null && HasChanges();
        SaveState.IsFocused = _onSave;
        NormalHints.Visibility = !_onSave && _grabbed is null ? Visibility.Visible : Visibility.Collapsed;
        GrabHints.Visibility = _grabbed is not null ? Visibility.Visible : Visibility.Collapsed;
        SaveHints.Visibility = _onSave && _grabbed is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TrySave()
    {
        if (SaveState.IsEnabled) Close(save: true);
    }

    private void SaveButton_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _onSave = true;
        UpdateFocusVisuals();
        TrySave();
    }

    private void ItemRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReorderItem item }) return;

        int index = Items.IndexOf(item);
        if (index < 0) return;

        if (_grabbed is null)
        {
            _onSave = false;
            _selectedIndex = index;
            UpdateFocusVisuals();
        }
        else if (index != _selectedIndex && _selectedIndex >= 0 && _selectedIndex < Items.Count)
        {
            // つかんでいる最中に別の行をクリックしたら、つかんでいる行をそこへ動かして置く。
            Items.Move(_selectedIndex, index);
            _selectedIndex = index;
            UpdateFocusVisuals();
        }
        ToggleGrab();
    }

    public void HandleGamepadInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                if (_onSave) { _onSave = false; UpdateFocusVisuals(); RefreshSaveState(); }
                else Move(-1);
                break;
            case GamepadButton.DPadDown:
                if (!_onSave) Move(1);
                break;
            case GamepadButton.DPadRight:
                if (!_onSave && _grabbed is null && Items.Count > 0)
                {
                    _onSave = true;
                    UpdateFocusVisuals();
                    RefreshSaveState();
                }
                break;
            case GamepadButton.DPadLeft:
                if (_onSave) { _onSave = false; UpdateFocusVisuals(); RefreshSaveState(); }
                break;
            case GamepadButton.A:
                if (_onSave) TrySave();
                else ToggleGrab();
                break;
            case GamepadButton.B:
                if (_grabbed is not null)
                {
                    // つかむ前の位置へ戻して置く。
                    Items.Move(_selectedIndex, _grabStartIndex);
                    _selectedIndex = _grabStartIndex;
                    Release();
                }
                else
                {
                    Close(save: false);
                }
                break;
        }
    }

    private void ToggleGrab()
    {
        if (Items.Count == 0) return;

        if (_grabbed is null)
        {
            _selectedIndex = Math.Clamp(_selectedIndex, 0, Items.Count - 1);
            _grabbed = Items[_selectedIndex];
            _grabStartIndex = _selectedIndex;
            _grabbed.IsGrabbed = true;
            RefreshSaveState();
        }
        else
        {
            Release();
        }
    }

    private void Release()
    {
        if (_grabbed is not null) _grabbed.IsGrabbed = false;
        _grabbed = null;
        UpdateFocusVisuals();
        RefreshSaveState();
        BringSelectedIntoView();
    }

    private void Move(int delta)
    {
        if (Items.Count == 0) return;

        int current = Math.Clamp(_selectedIndex, 0, Items.Count - 1);
        int next = current + delta;

        if (_grabbed is not null)
        {
            // つかんでいる間は端で止める(折り返さない)。
            if (next < 0 || next >= Items.Count) return;
            Items.Move(current, next);
            _selectedIndex = next;
        }
        else
        {
            _selectedIndex = GamepadListNavigator.Move(current, delta, Items.Count, wrap: true);
        }

        UpdateFocusVisuals();
        BringSelectedIntoView();
    }

    /// <summary>行の高さ(XAMLのDataTemplateのHeightと同じ値にすること)。</summary>
    private const double RowHeight = 84;

    /// <summary>
    /// 選択中の行が見えるようにスクロールする。行を動かすと(ObservableCollection.Move)コンテナが
    /// 作り直されて、ContainerFromIndex/StartBringIntoViewが当てにならないため、行の高さが固定で
    /// あることを使って、位置(index×高さ)から直接スクロール量を計算する。
    /// </summary>
    private void BringSelectedIntoView()
    {
        double top = _selectedIndex * RowHeight;
        double bottom = top + RowHeight;
        double offset = ListScrollViewer.VerticalOffset;
        double viewport = ListScrollViewer.ViewportHeight;
        if (viewport <= 0) return;

        double? target = null;
        if (top < offset) target = top;
        else if (bottom > offset + viewport) target = bottom - viewport;

        if (target.HasValue)
        {
            ListScrollViewer.ChangeView(null, Math.Max(0, target.Value), null, disableAnimation: true);
        }
    }

    private void UpdateFocusVisuals()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            // 一覧の枠は、保存ボタンにフォーカスがある間は消す。
            Items[i].IsFocused = !_onSave && i == _selectedIndex;
        }
        RefreshSaveState();
    }
}

/// <summary><see cref="ReorderOverlay.Items"/>の1件。</summary>
public sealed class ReorderItem : INotifyPropertyChanged
{
    /// <summary>並び順の結果として返す識別子(コンソールId/コレクションId)。</summary>
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; Raise(nameof(IsFocused)); }
    }

    private bool _isGrabbed;
    public bool IsGrabbed
    {
        get => _isGrabbed;
        set { if (_isGrabbed == value) return; _isGrabbed = value; Raise(nameof(IsGrabbed)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
