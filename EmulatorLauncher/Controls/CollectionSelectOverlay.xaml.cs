using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EmulatorLauncher.Converters;
using EmulatorLauncher.Input;
using EmulatorLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Controls;

/// <summary>コレクション選択画面の1行(アイコン+名前+ゲーム数)。</summary>
public sealed class CollectionSelectItem : INotifyPropertyChanged
{
    public RomCollectionInfo Collection { get; set; } = null!;
    public string Name => Collection.Name;
    public ImageSource? Icon { get; set; }
    public string CountText { get; set; } = string.Empty;

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 画面全体を覆う「コレクション選択画面」。使い方はCollectionSelectOverlay.xamlのコメント参照。
/// 作りはFullScreenChoiceOverlayと同じ(十字キー上下で移動、Aで決定、Bでキャンセル)。
/// </summary>
public sealed partial class CollectionSelectOverlay : UserControl
{
    private static readonly IconPathToImageSourceConverter IconConverter = new();
    private const double RowPitch = 112;

    public ObservableCollection<CollectionSelectItem> Items { get; } = new();
    public bool IsOpen { get; private set; }

    private int _selectedIndex;
    private TaskCompletionSource<RomCollectionInfo?>? _tcs;

    public CollectionSelectOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>選択画面を表示し、選ばれる(Bでキャンセル=null)まで待つ。</summary>
    public Task<RomCollectionInfo?> ShowAsync(string title, IEnumerable<RomCollectionInfo> collections)
    {
        TitleText.Text = title;

        Items.Clear();
        foreach (RomCollectionInfo c in collections)
        {
            ImageSource? icon = null;
            try { icon = IconConverter.Convert(c.IconPath, typeof(object), null!, "") as ImageSource; } catch { }
            Items.Add(new CollectionSelectItem
            {
                Collection = c,
                Icon = icon,
                CountText = $"{c.GameIds.Count}本",
            });
        }

        _selectedIndex = 0;
        UpdateFocusVisuals();
        ListScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        Visibility = Visibility.Visible;
        IsOpen = true;

        _tcs = new TaskCompletionSource<RomCollectionInfo?>();
        return _tcs.Task;
    }

    private void Close(RomCollectionInfo? result)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        TaskCompletionSource<RomCollectionInfo?>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    private void ItemRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CollectionSelectItem item }) Close(item.Collection);
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
                if (_selectedIndex >= 0 && _selectedIndex < Items.Count) Close(Items[_selectedIndex].Collection);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;

        _selectedIndex = GamepadListNavigator.Move(
            Math.Clamp(_selectedIndex, 0, Items.Count - 1), delta, Items.Count, wrap: true);
        UpdateFocusVisuals();

        if (ItemsHost.ContainerFromIndex(_selectedIndex) is FrameworkElement container)
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
    }

    private void UpdateFocusVisuals()
    {
        for (int i = 0; i < Items.Count; i++) Items[i].IsFocused = i == _selectedIndex;
    }
}
