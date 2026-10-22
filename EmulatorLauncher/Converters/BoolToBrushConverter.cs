using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI;

namespace EmulatorLauncher.Converters;

/// <summary>IsSelected(bool) を選択ハイライト色 / 透明ブラシに変換する。</summary>
public class BoolToBrushConverter : IValueConverter
{
    private static SolidColorBrush? _pulseBrush;
    private static Storyboard? _pulseStoryboard;
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    private static SolidColorBrush GetPulseBrush()
    {
        if (_pulseBrush != null) return _pulseBrush;

        var baseColor = Windows.UI.Color.FromArgb(255, 0x00, 0xC3, 0xE3);
        var brightColor = Windows.UI.Color.FromArgb(255, 0xA6, 0xF4, 0xFF);
        _pulseBrush = new SolidColorBrush(baseColor);

        try
        {
            var animation = new ColorAnimation
            {
                From = baseColor,
                To = brightColor,
                Duration = new Duration(TimeSpan.FromMilliseconds(900)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, _pulseBrush);
            Storyboard.SetTargetProperty(animation, "Color");
            _pulseStoryboard = new Storyboard();
            _pulseStoryboard.Children.Add(animation);
            _pulseStoryboard.Begin();
        }
        catch
        {
            // アニメーションが使えなくても固定色の枠として動作する。
        }

        return _pulseBrush;
    }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isSelected = value is bool b && b;
        return isSelected ? GetPulseBrush() : TransparentBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
