using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 画面全体を覆う、複数の選択肢から1つを選ぶ画面。使い方はFullScreenChoiceOverlay.xamlのコメント参照。
/// 選択肢にはGamepadListPickerOverlayと同じGamepadPickerItem(IsCheckedで現在値を示す)を使う。
/// </summary>
public sealed partial class FullScreenChoiceOverlay : UserControl
{
    public ObservableCollection<GamepadPickerItem> Items { get; } = new();

    public bool IsOpen { get; private set; }

    private int _selectedIndex;

    /// <summary>Show中の待ち合わせ用。Close()で(選ばれた項目、またはnull)を渡して完了させる。</summary>
    private TaskCompletionSource<GamepadPickerItem?>? _tcs;

    public FullScreenChoiceOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>
    /// 選択画面を表示し、選ばれる(またはキャンセルされる)まで待つ。
    /// 戻り値はA/クリックで選ばれた項目、Bでキャンセルされた場合はnull。
    /// 最初のフォーカスはIsChecked(現在値)の行、無ければ先頭。
    /// </summary>
    public Task<GamepadPickerItem?> ShowAsync(string title, IEnumerable<GamepadPickerItem> items)
    {
        TitleText.Text = title;

        Items.Clear();
        int checkedIndex = -1;
        foreach (GamepadPickerItem item in items)
        {
            if (item.IsChecked && checkedIndex < 0) checkedIndex = Items.Count;
            Items.Add(item);
        }

        _selectedIndex = Math.Max(checkedIndex, 0);
        UpdateFocusVisuals();
        ListScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        Visibility = Visibility.Visible;
        IsOpen = true;

        // 現在値の行が画面外にある場合に備えて、表示後にその行までスクロールする。
        // 2回目以降に開くときは、一覧の組み直しとレイアウトがまだ終わっていないことがあるため、
        // まずレイアウトを確定させて行の位置から計算し、そのあと念のため実際の行でも位置を合わせる。
        ListScrollViewer.UpdateLayout();
        ScrollSelectedByEstimate();
        DispatcherQueue.TryEnqueue(() =>
        {
            ListScrollViewer.UpdateLayout();
            BringSelectedIntoView(animated: false);
        });

        _tcs = new TaskCompletionSource<GamepadPickerItem?>();
        return _tcs.Task;
    }

    private void Close(GamepadPickerItem? result)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        TaskCompletionSource<GamepadPickerItem?>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    private void ItemRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GamepadPickerItem item }) return;

        int index = Items.IndexOf(item);
        if (index >= 0)
        {
            _selectedIndex = index;
            UpdateFocusVisuals();
        }

        Close(item);
    }

    public void HandleGamepadInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                Close(null);
                break;
            case GamepadButton.DPadUp:
                MoveSelection(-1);
                break;
            case GamepadButton.DPadDown:
                MoveSelection(1);
                break;
            case GamepadButton.A:
                if (_selectedIndex >= 0 && _selectedIndex < Items.Count)
                {
                    Close(Items[_selectedIndex]);
                }
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;

        _selectedIndex = GamepadListNavigator.Move(
            Math.Clamp(_selectedIndex, 0, Items.Count - 1), delta, Items.Count, wrap: true);
        UpdateFocusVisuals();
        BringSelectedIntoView(animated: true);
    }

    /// <summary>1行の高さ(MinHeight)から、フォーカス中の行が中央付近に来る位置へスクロールする。</summary>
    private void ScrollSelectedByEstimate()
    {
        const double RowPitch = 92;
        double viewport = ListScrollViewer.ViewportHeight;
        if (viewport <= 0 || _selectedIndex < 0) return;

        double top = _selectedIndex * RowPitch;
        if (top >= ListScrollViewer.VerticalOffset && top + RowPitch <= ListScrollViewer.VerticalOffset + viewport) return;

        ListScrollViewer.ChangeView(null, Math.Max(0, top + RowPitch / 2 - viewport / 2), null, disableAnimation: true);
    }

    private void BringSelectedIntoView(bool animated)
    {
        if (ItemsHost.ContainerFromIndex(_selectedIndex) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = animated });
        }
    }

    private void UpdateFocusVisuals()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].IsFocused = i == _selectedIndex;
        }
    }
}
