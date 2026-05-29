using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace WidgetTemplate
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is Visibility v && v == Visibility.Visible;
    }

    public class BoolToInverseVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is Visibility v && v == Visibility.Collapsed;
    }

    public class LaunchedToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => (value is bool b && b) ? 0.3 : 1.0;
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    public class RunningToPlayForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool running = value is bool b && b;
            return new Windows.UI.Xaml.Media.SolidColorBrush(
                running
                    ? Windows.UI.Color.FromArgb(255, 76, 175, 80)     // #4CAF50 green
                    : Windows.UI.Color.FromArgb(119, 255, 255, 255));  // #77FFFFFF dim
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}