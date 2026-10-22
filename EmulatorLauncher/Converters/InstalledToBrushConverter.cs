using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Converters;

/// <summary>LibretroCoreListItem.Installed(bool)を、コア管理画面(SettingsPage)の状態表示テキスト用の色(インストール済み=緑系、未インストール=グレー系)に変換する。</summary>
public class InstalledToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool installed = value is bool b && b;
        return installed
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2E, 0x7D, 0x32))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8A));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
