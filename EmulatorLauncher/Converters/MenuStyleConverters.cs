using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Converters;

/// <summary>
/// 中断メニュー(GamePlayPage)の選択行の背景。選択中は白のベタ塗り、未選択は透明。
/// Switchの中断メニューのように「白い帯+赤い文字」で選択行を示すために使う
/// (文字色はSelectedToMenuForegroundConverterと組み合わせる)。
/// </summary>
public class BoolToMenuHighlightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isSelected = value is bool b && b;
        // ダークテーマの画面(設定など)では、白ベタではなく少し明るいグレーの帯にする。
        Windows.UI.Color selected = EmulatorLauncher.Views.AppThemeRouter.IsDarkActive
            ? Windows.UI.Color.FromArgb(0xFF, 0x36, 0x40, 0x5A)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        return isSelected
            ? new SolidColorBrush(selected)
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// 中断メニュー(GamePlayPage)の文字色。選択中は赤(Switchの選択行と同じ)、未選択は白。
/// 背景はBoolToMenuHighlightConverter(選択中=白)とセットで使う。
/// </summary>
public class SelectedToMenuForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isSelected = value is bool b && b;
        return isSelected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0x00, 0x12))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// 巻き戻し画面のフィルムストリップ用。選択中のコマは白枠、未選択は枠なし(透明)。
/// </summary>
public class BoolToFilmstripBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isSelected = value is bool b && b;
        return isSelected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// 設定画面の右側の行の状態表示(「使用中」/「未使用」等)の文字色。
/// trueならSwitchの青、falseならグレー(Switchの「ON」「OFF」と同じ使い分け)。
/// </summary>
public class BoolToStatusTextBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Blue = new(Windows.UI.Color.FromArgb(0xFF, 0x30, 0x50, 0xEF));
    private static readonly SolidColorBrush BlueDark = new(Windows.UI.Color.FromArgb(0xFF, 0x7C, 0x9B, 0xFF));
    private static readonly SolidColorBrush Gray = new(Windows.UI.Color.FromArgb(0xFF, 0xA6, 0xA6, 0xA6));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? (EmulatorLauncher.Views.AppThemeRouter.IsDarkActive ? BlueDark : Blue) : Gray;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// 設定画面の左メニュー用の文字色。選択中のカテゴリはSwitchの設定画面と同じ青、
/// それ以外は通常の文字色(AppTextPrimaryBrush)。
/// </summary>
public class BoolToSwitchBlueTextConverter : IValueConverter
{
    private static readonly SolidColorBrush Blue = new(Windows.UI.Color.FromArgb(0xFF, 0x30, 0x50, 0xEF));
    private static readonly SolidColorBrush Normal = new(Windows.UI.Color.FromArgb(0xFF, 0x2D, 0x2D, 0x2D));
    private static readonly SolidColorBrush BlueDark = new(Windows.UI.Color.FromArgb(0xFF, 0x7C, 0x9B, 0xFF));
    private static readonly SolidColorBrush NormalDark = new(Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xEC, 0xF4));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool dark = EmulatorLauncher.Views.AppThemeRouter.IsDarkActive;
        return value is bool b && b ? (dark ? BlueDark : Blue) : (dark ? NormalDark : Normal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
