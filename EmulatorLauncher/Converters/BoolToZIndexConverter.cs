using System;
using Microsoft.UI.Xaml.Data;

namespace EmulatorLauncher.Converters;

/// <summary>CollectionGameSelectPage専用。</summary>
public class BoolToZIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 1 : 0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
