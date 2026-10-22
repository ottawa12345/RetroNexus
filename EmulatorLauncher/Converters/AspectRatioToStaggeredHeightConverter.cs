using System;
using Microsoft.UI.Xaml.Data;

namespace EmulatorLauncher.Converters;

public class AspectRatioToStaggeredHeightConverter : IValueConverter
{
    // 全体の高さを底上げする倍率 (1.25 で縦方向に25%拡大)
    private const double HeightMultiplier = 1.39;

    // クランプの下限・上限を大幅に広げる
    private const double MinHeightRatio = 0.6;  // 非常に横長のカードの下限
    private const double MaxHeightRatio = 3.0;  // 非常に縦長のカードの上限

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double ratio = value is double d && d > 0 ? d : 1.0;
        double columnWidth = double.TryParse(parameter as string, out double w) && w > 0 ? w : 220.0;

        double height = columnWidth / ratio;
        double minHeight = columnWidth * MinHeightRatio;
        double maxHeight = columnWidth * MaxHeightRatio;
        return Math.Clamp(height, minHeight, maxHeight);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}