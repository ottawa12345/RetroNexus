using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 1個選ぶだけの一覧選択オーバーレイ。使い方はGamepadListPickerOverlay.xamlのコメント参照。
/// </summary>
public sealed partial class GamepadListPickerOverlay : UserControl
{
    public ObservableCollection<GamepadPickerItem> Items { get; } = new();

    public bool IsOpen { get; private set; }

    private int _selectedIndex;

    /// <summary>Show中の待ち合わせ用。Close()が呼ばれた時点で(選ばれた項目、またはnull)を渡して完了させる。</summary>
    private TaskCompletionSource<GamepadPickerItem?>? _tcs;

    public GamepadListPickerOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>一覧を表示し、選択される(またはキャンセルされる)まで待つ。
    /// 戻り値はA/クリックで選ばれた項目、Bでキャンセルされた場合はnull。</summary>
    public Task<GamepadPickerItem?> ShowAsync(string title, IEnumerable<GamepadPickerItem> items)
    {
        TitleText.Text = title;

        Items.Clear();
        foreach (GamepadPickerItem item in items)
        {
            Items.Add(item);
        }

        _selectedIndex = 0;
        UpdateFocusVisuals();
        ListScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        Visibility = Visibility.Visible;
        IsOpen = true;

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

    private void ItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GamepadPickerItem item }) return;

        int index = Items.IndexOf(item);
        if (index >= 0) SetSelectedIndex(index);

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
                SetSelectedIndex(_selectedIndex - 1);
                break;
            case GamepadButton.DPadDown:
                SetSelectedIndex(_selectedIndex + 1);
                break;
            case GamepadButton.A:
                if (_selectedIndex >= 0 && _selectedIndex < Items.Count)
                {
                    Close(Items[_selectedIndex]);
                }
                break;
        }
    }

    private void SetSelectedIndex(int index)
    {
        if (Items.Count == 0) return;

        _selectedIndex = GamepadListNavigator.Move(
            Math.Clamp(_selectedIndex, 0, Items.Count - 1), index - _selectedIndex, Items.Count, wrap: true);
        UpdateFocusVisuals();

        if (ItemsHost.ContainerFromIndex(_selectedIndex) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
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

/// <summary>GamepadListPickerOverlay.Itemsの1件。</summary>
public sealed class GamepadPickerItem : INotifyPropertyChanged
{
    /// <summary>一覧に表示するラベル。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>呼び出し側が選択結果を特定するための任意の値(例: ConsoleInfo)。</summary>
    public object? Tag { get; set; }

    /// <summary>現在の設定値かどうか(FullScreenChoiceOverlayで右端にチェックマークを表示する)。</summary>
    public bool IsChecked { get; set; }

    private bool _isFocused;

    /// <summary>ゲームパッドの選択枠が乗っているか。</summary>
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
