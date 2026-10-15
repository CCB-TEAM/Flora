using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace FModel.Views.Resources.Converters;

public class BoolToVisibilityConverter : MarkupExtension, IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;
        bool inverse = parameter?.ToString()?.Equals("Inverse", StringComparison.OrdinalIgnoreCase) ?? false;

        if (inverse)
            boolValue = !boolValue;

        bool useHidden = parameter?.ToString()?.Equals("Hidden", StringComparison.OrdinalIgnoreCase) ?? false;

        return boolValue ? Visibility.Visible : (useHidden ? Visibility.Hidden : Visibility.Collapsed);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            bool result = visibility == Visibility.Visible;
            bool inverse = parameter?.ToString()?.Equals("Inverse", StringComparison.OrdinalIgnoreCase) ?? false;
            return inverse ? !result : result;
        }
        return false;
    }
}
