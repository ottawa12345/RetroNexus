using System;
using System.Threading.Tasks;
using EmulatorLauncher.Converters;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 確認ダイアログ。使い方はConfirmDialogOverlay.xamlのコメント参照。
/// </summary>
public sealed partial class ConfirmDialogOverlay : UserControl
{
    private static readonly BoolToBrushConverter FrameBrushConverter = new();
    private static readonly BoolToMenuHighlightConverter BackgroundConverter = new();

    public bool IsOpen { get; private set; }

    /// <summary>true=右(実行)ボタンにフォーカス、false=左(キャンセル)ボタンにフォーカス。</summary>
    private bool _confirmFocused;

    private TaskCompletionSource<bool>? _tcs;

    public ConfirmDialogOverlay()
    {
        this.InitializeComponent();
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>
    /// 確認ダイアログを表示し、選ばれるまで待つ。戻り値は実行ボタンが選ばれたらtrue、
    /// キャンセル(キャンセルボタン/Bボタン)ならfalse。最初のフォーカスはキャンセル側。
    /// </summary>
    /// <summary>ダーク配色のときは、暗い背景に溶けないよう本体を少し明るい色にする(ライトは従来のまま)。</summary>
    private void ApplyDialogTheme()
    {
        if (EmulatorLauncher.Views.AppThemeRouter.IsDarkActive)
        {
            DialogBody.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x2F, 0x3A));
        }
        else
        {
            DialogBody.Background = (Brush)Application.Current.Resources["AppBackgroundBrush"];
        }
    }

    public Task<bool> ShowAsync(string message, string confirmLabel, string cancelLabel = "キャンセル")
    {
        MessageText.Text = message;
        ApplyDialogTheme();
        ConfirmText.Text = confirmLabel;
        CancelText.Text = cancelLabel;
        ConfirmButtonGrid.Visibility = Visibility.Visible;
        Grid.SetColumnSpan(CancelButtonGrid, 1);

        _confirmFocused = false;
        UpdateFocusVisuals();

        Visibility = Visibility.Visible;
        IsOpen = true;

        _tcs = new TaskCompletionSource<bool>();
        return _tcs.Task;
    }

    /// <summary>
    /// 警告を表示し、「閉じる」だけのボタンで閉じられるまで待つ(実行/キャンセルの選択は無い)。
    /// </summary>
    public async Task AlertAsync(string message, string closeLabel = "閉じる")
    {
        Task<bool> task = ShowAsync(message, closeLabel, closeLabel);
        ConfirmButtonGrid.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(CancelButtonGrid, 2);
        await task;
    }

    private void Close(bool result)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        TaskCompletionSource<bool>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    private void CancelButton_Tapped(object sender, TappedRoutedEventArgs e) => Close(false);

    private void ConfirmButton_Tapped(object sender, TappedRoutedEventArgs e) => Close(true);

    public void HandleGamepadInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                Close(false);
                break;
            case GamepadButton.DPadLeft:
                _confirmFocused = false;
                UpdateFocusVisuals();
                break;
            case GamepadButton.DPadRight:
                _confirmFocused = true;
                UpdateFocusVisuals();
                break;
            case GamepadButton.A:
                Close(_confirmFocused);
                break;
        }
    }

    private void UpdateFocusVisuals()
    {
        ApplyFrame(CancelFrame, !_confirmFocused);
        ApplyFrame(ConfirmFrame, _confirmFocused);
    }

    private static void ApplyFrame(Microsoft.UI.Xaml.Controls.Border frame, bool focused)
    {
        frame.BorderBrush = (Brush)FrameBrushConverter.Convert(focused, typeof(Brush), null!, string.Empty);
        frame.Background = (Brush)BackgroundConverter.Convert(focused, typeof(Brush), null!, string.Empty);
    }
}
