using System;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmulatorLauncher.Controls;

/// <summary>OKボタンだけの簡単な案内オーバーレイ。</summary>
public sealed partial class GamepadMessageOverlay : UserControl
{
    public bool IsOpen { get; private set; }

    /// <summary>Show中の待ち合わせ用。</summary>
    private TaskCompletionSource<bool>? _tcs;

    public GamepadMessageOverlay()
    {
        this.InitializeComponent();
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>案内を表示し、閉じられるまで待つ。</summary>
    public Task ShowAsync(string title, string message)
    {
        TitleText.Text = title;
        MessageText.Text = message;

        Visibility = Visibility.Visible;
        IsOpen = true;

        _tcs = new TaskCompletionSource<bool>();
        return _tcs.Task;
    }

    private void Close()
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        TaskCompletionSource<bool>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(true);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>A/Bどちらでも閉じる(内容を確認さえしてもらえればよい単純な案内のため)。</summary>
    public void HandleGamepadInput(GamepadButton button)
    {
        if (button == GamepadButton.A || button == GamepadButton.B)
        {
            Close();
        }
    }
}
