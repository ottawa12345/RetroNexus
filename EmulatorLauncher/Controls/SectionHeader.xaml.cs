using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmulatorLauncher.Controls;

/// <summary>パネル見出し(SectionHeader)共通UserControl。</summary>
public sealed partial class SectionHeader : UserControl
{
    public SectionHeader()
    {
        this.InitializeComponent();
    }

    /// <summary>見出しタイトル(例: "コア管理")。</summary>
    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>件数表示(例: "(12件)")。</summary>
    public string? CountText
    {
        get => CountTextBlock.Text;
        set
        {
            CountTextBlock.Text = value ?? string.Empty;
            CountTextBlock.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>読み込み中インジケーター(ProgressRing)の表示/非表示。</summary>
    public bool IsLoading
    {
        get => LoadingRing.IsActive;
        set => LoadingRing.IsActive = value;
    }

    /// <summary>右端の操作ボタンの文言。</summary>
    public string? ActionButtonText
    {
        get => ActionButtonElement.Content as string;
        set
        {
            ActionButtonElement.Content = value;
            ActionButtonElement.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>右端の操作ボタンを押せる状態かどうか(処理中の二重クリック防止等に使う)。</summary>
    public bool IsActionEnabled
    {
        get => ActionButtonElement.IsEnabled;
        set => ActionButtonElement.IsEnabled = value;
    }

    /// <summary>操作ボタンが押されたときに発生する。</summary>
    public event RoutedEventHandler? ActionClick;

    private void ActionButtonElement_Click(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, e);
}
