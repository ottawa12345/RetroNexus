using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using EmulatorLauncher.Models;
using EmulatorLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmulatorLauncher.Controls;

/// <summary>
/// コンソールの編集の全画面(コンソール選択画面でXボタン)。画面構成・操作はLauncherEditOverlayに準拠
/// (使い方はConsoleEditOverlay.xamlのコメント参照)。
/// アイコン(Consoles.jsonのiconPath)とデフォルトランチャー(launcherName)を変更できる。
///
/// 使う側(ConsoleSelectPage):
/// ・<c>Show(console)</c> で開く。表示中のゲームパッド入力は <see cref="HandleGamepadInput"/> へ渡す。
/// ・閉じると <see cref="Closed"/> が発火する。引数は「保存されたか」で、trueならコンソール一覧を読み直す。
///
/// アイコンの選択はIconPickerOverlay(ダイアログ風の画面)、ランチャーの選択はFullScreenChoiceOverlayへ
/// 入れ子で出す。表示中はそちらへ入力を渡す。
/// </summary>
public sealed partial class ConsoleEditOverlay : UserControl
{
    private const string KeyConsole = "console";
    private const string KeyIcon = "icon";
    private const string KeyLauncher = "launcher";

    private const double RowPitch = 92;

    public ObservableCollection<EditFormRow> Rows { get; } = new();

    /// <summary>「保存する」ボタンの状態(x:Bindで表示)。</summary>
    public LauncherSaveButtonState SaveState { get; } = new();

    public bool IsOpen { get; private set; }

    /// <summary>閉じたときに発火する。引数は「保存されたか」。</summary>
    public event EventHandler<bool>? Closed;

    private ConsoleInfo? _console;
    private List<LauncherInfo> _launchers = new();
    private int _launcherIndex = -1;
    private int _originalLauncherIndex = -1;

    private string _iconPath = string.Empty;
    private string _originalIconPath = string.Empty;

    private int _focusIndex;
    private bool _onSave;
    private bool _busy;

    public ConsoleEditOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    // ==================================================================
    // 表示/非表示
    // ==================================================================

    public void Show(ConsoleInfo console)
    {
        _console = console;
        _busy = false;
        _onSave = false;
        ErrorText.Visibility = Visibility.Collapsed;

        _iconPath = console.IconPath ?? string.Empty;
        _originalIconPath = _iconPath;

        _launchers = SampleData.GetLaunchers()
            .Where(l => l.Id == console.Id)
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _launcherIndex = _launchers.FindIndex(l => l.Name == console.LauncherName);
        if (_launcherIndex < 0 && _launchers.Count > 0) _launcherIndex = 0;
        _originalLauncherIndex = _launcherIndex;

        Rows.Clear();
        Rows.Add(new EditFormRow { Key = KeyConsole, IsInfo = true });
        Rows.Add(new EditFormRow { Key = KeyIcon });
        Rows.Add(new EditFormRow { Key = KeyLauncher });
        _focusIndex = 1;

        ChoiceOverlay.Visibility = Visibility.Collapsed;
        IconPicker.Visibility = Visibility.Collapsed;
        ConfirmOverlay.Visibility = Visibility.Collapsed;

        Refresh();

        Visibility = Visibility.Visible;
        IsOpen = true;
        RowsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>変更があるまま戻ろうとしたとき、捨ててよいか確認する(不意に戻って変更が消えないようにする)。</summary>
    private bool IsDirty() => _launcherIndex != _originalLauncherIndex || _iconPath != _originalIconPath;

    private async Task ConfirmDiscardAsync()
    {
        bool discard = await ConfirmOverlay.ShowAsync("保存していない変更があります。変更を捨てて戻りますか?", "変更を捨てる", "編集を続ける");
        if (discard && IsOpen) Close(saved: false);
    }

    private void Close(bool saved)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;
        Closed?.Invoke(this, saved);
    }

    // ==================================================================
    // 表示の更新
    // ==================================================================

    private LauncherInfo? CurrentLauncher
        => _launcherIndex >= 0 && _launcherIndex < _launchers.Count ? _launchers[_launcherIndex] : null;

    private void Refresh()
    {
        foreach (EditFormRow row in Rows)
        {
            switch (row.Key)
            {
                case KeyConsole:
                    row.Label = "コンソール";
                    row.DisplayValue = _console?.Name ?? string.Empty;
                    row.IsPlaceholder = false;
                    break;

                case KeyIcon:
                    row.Label = "アイコン";
                    bool hasIcon = !string.IsNullOrEmpty(_iconPath);
                    row.IsPlaceholder = !hasIcon;
                    row.DisplayValue = hasIcon ? Path.GetFileName(_iconPath) : "なし";
                    row.IconSource = hasIcon ? CollectionEditOverlay.CreateThumbnail(_iconPath) : null;
                    break;

                case KeyLauncher:
                    row.Label = "デフォルトランチャー";
                    LauncherInfo? launcher = CurrentLauncher;
                    row.IsPlaceholder = launcher is null;
                    row.DisplayValue = launcher?.Name ?? "ランチャーがありません";
                    break;
            }
        }

        UpdatePreview();
        SaveState.IsEnabled = _launcherIndex != _originalLauncherIndex
                              || !string.Equals(_iconPath, _originalIconPath, StringComparison.Ordinal);
        UpdateFocusFlags();
        RefreshHelpText();
    }

    private void RefreshHelpText()
    {
        if (_onSave)
        {
            HelpText.Text = "変更した内容を保存します。";
            return;
        }

        switch (FocusedKey)
        {
            case KeyConsole:
                HelpText.Text = "編集しているコンソールです。";
                break;
            case KeyIcon:
                HelpText.Text = "コンソール選択画面に表示されるアイコンです。";
                break;
            case KeyLauncher:
                LauncherInfo? launcher = CurrentLauncher;
                HelpText.Text = launcher is null
                    ? "設定の「ランチャー管理」からランチャーを追加できます。"
                    : launcher.IsExternal
                        ? $"外部コマンド: {launcher.ExePath}"
                        : $"libretroコア: {launcher.CorePath}";
                break;
            default:
                HelpText.Text = string.Empty;
                break;
        }
    }

    private void UpdatePreview()
    {
        if (!string.IsNullOrEmpty(_iconPath))
        {
            try
            {
                PreviewImage.Source = new BitmapImage(new Uri(_iconPath));
                PreviewImage.Visibility = Visibility.Visible;
                PreviewPlaceholderText.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // 下のフォールバックへ
            }
        }

        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewPlaceholderText.Visibility = Visibility.Visible;
    }

    private string? FocusedKey => _focusIndex >= 0 && _focusIndex < Rows.Count ? Rows[_focusIndex].Key : null;

    private void UpdateFocusFlags()
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsFocused = !_onSave && i == _focusIndex;
        }

        SaveState.IsFocused = _onSave;
    }

    private void ScrollToFocus()
    {
        double target = _focusIndex * RowPitch - 260;
        RowsScrollViewer.ChangeView(null, Math.Max(0, target), null, disableAnimation: true);
    }

    // ==================================================================
    // 入力
    // ==================================================================

    public void HandleGamepadInput(GamepadButton button)
    {
        if (!IsOpen) return;

        if (ConfirmOverlay.IsOpen)
        {
            ConfirmOverlay.HandleGamepadInput(button);
            return;
        }
        if (IconPicker.IsOpen)
        {
            IconPicker.HandleGamepadInput(button);
            return;
        }
        if (ChoiceOverlay.IsOpen)
        {
            ChoiceOverlay.HandleGamepadInput(button);
            return;
        }

        if (_busy) return;

        ErrorText.Visibility = Visibility.Collapsed;

        switch (button)
        {
            case GamepadButton.B:
                if (IsDirty()) _ = ConfirmDiscardAsync();
                else Close(saved: false);
                break;

            case GamepadButton.DPadUp:
                if (_onSave) SetSaveFocus(false);
                else MoveFocus(-1);
                break;
            case GamepadButton.DPadDown:
                if (!_onSave) MoveFocus(1);
                break;

            case GamepadButton.DPadRight:
                if (!_onSave) SetSaveFocus(true);
                break;
            case GamepadButton.DPadLeft:
                if (_onSave) SetSaveFocus(false);
                break;

            case GamepadButton.A:
                if (_onSave) TrySave();
                else ActivateFocused();
                break;
        }
    }

    private void SetSaveFocus(bool onSave)
    {
        _onSave = onSave;
        UpdateFocusFlags();
        RefreshHelpText();
    }

    /// <summary>フォーカスできる行(表示専用の行を除く)だけを順に移動する。</summary>
    private void MoveFocus(int delta)
    {
        if (Rows.Count == 0) return;

        int index = _focusIndex;
        for (int step = 0; step < Rows.Count; step++)
        {
            index = GamepadListNavigator.Move(index, delta, Rows.Count, wrap: true);
            if (!Rows[index].IsInfo)
            {
                _focusIndex = index;
                break;
            }
        }

        UpdateFocusFlags();
        RefreshHelpText();
        ScrollToFocus();
    }

    private void ActivateFocused()
    {
        switch (FocusedKey)
        {
            case KeyIcon:
                _ = PickIconAsync();
                break;
            case KeyLauncher:
                _ = PickLauncherAsync();
                break;
        }
    }

    private async Task PickIconAsync()
    {
        string? picked = await IconPicker.ShowAsync(
            "アイコンを選択",
            new[] { CollectionEditOverlay.ConsoleIconsFolder, CollectionEditOverlay.UserIconsFolder },
            _iconPath);

        if (picked is not null && !string.Equals(picked, _iconPath, StringComparison.Ordinal))
        {
            _iconPath = picked;
            Refresh();
        }
    }

    private async Task PickLauncherAsync()
    {
        if (_launchers.Count == 0)
        {
            ShowError("ランチャーが登録されていません。設定の「ランチャー管理」から追加できます。");
            return;
        }

        var items = _launchers
            .Select((l, i) => new GamepadPickerItem { Label = l.Name, Tag = i, IsChecked = i == _launcherIndex })
            .ToList();

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync("デフォルトランチャー", items);
        if (picked?.Tag is int index && index != _launcherIndex)
        {
            _launcherIndex = index;
            Refresh();
        }
    }

    /// <summary>マウス/タッチ: 行をクリックしたらフォーカスを移してAと同じ動作にする。</summary>
    private void RowsItemsControl_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsOpen || _busy || ChoiceOverlay.IsOpen || IconPicker.IsOpen || ConfirmOverlay.IsOpen) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not EditFormRow row || row.IsInfo) return;

        int index = Rows.IndexOf(row);
        if (index < 0) return;

        ErrorText.Visibility = Visibility.Collapsed;
        _focusIndex = index;
        _onSave = false;
        UpdateFocusFlags();
        RefreshHelpText();
        ActivateFocused();
    }

    private void SaveButton_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsOpen || _busy || ChoiceOverlay.IsOpen || IconPicker.IsOpen || ConfirmOverlay.IsOpen) return;

        ErrorText.Visibility = Visibility.Collapsed;
        SetSaveFocus(true);
        TrySave();
    }

    // ==================================================================
    // 保存
    // ==================================================================

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>変更が無くグレーの間は、何もせず(変更が無いことを知らせて)保存しない。</summary>
    private void TrySave()
    {
        if (!SaveState.IsEnabled)
        {
            ShowError("変更はありません。");
            return;
        }

        Save();
    }

    private void Save()
    {
        if (_console is null) return;

        try
        {
            LauncherConfigService.UpdateConsole(_console.Id, _iconPath, CurrentLauncher?.Name);
        }
        catch (Exception ex)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
            return;
        }

        Close(saved: true);
    }
}
