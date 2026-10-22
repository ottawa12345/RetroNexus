using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmulatorLauncher.Controls;

public sealed partial class SettingsGuideHeader : UserControl
{
    public SettingsGuideHeader()
    {
        this.InitializeComponent();
    }

    /// <summary>Assets/guide内の画像名(拡張子なし)。</summary>
    public string Image
    {
        set => GuideImage.Source = new BitmapImage(new Uri($"ms-appx:///Assets/guide/{value}.png"));
    }

    public string Text
    {
        set => GuideText.Text = value;
    }
}
