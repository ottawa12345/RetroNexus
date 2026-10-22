using Microsoft.UI.Xaml.Data;

namespace EmulatorLauncher.Converters;

/// <summary>RomFile.AspectRatio(幅÷高さ)を、GameSelectPageのカード(Border)のWidth/Heightへ変換する。</summary>

public class AspectRatioToCardSizeConverter : IValueConverter
{
    /// <summary>カードが収まる正方形の枠の一辺(px)。</summary>
    // private const double MaxSide = 280.0;
    private const double MaxSide = 420.0;
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double ratio = value is double d && d > 0 ? d : 1.0;
        bool isWidth = string.Equals(parameter as string, "Width", StringComparison.OrdinalIgnoreCase);

        double width = ratio >= 1.0 ? MaxSide : MaxSide * ratio;
        double height = ratio >= 1.0 ? MaxSide / ratio : MaxSide;

        return isWidth ? width : height;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
