using System;
using System.Globalization;
using System.Windows.Data;

namespace FModel.Views.Resources.Converters;

public class BoolToToggleConverter : IValueConverter
{
    public static readonly BoolToToggleConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            true => FModel.Localization.LocalizationManager.Instance.Get("Common_Enabled"),
            _ => FModel.Localization.LocalizationManager.Instance.Get("Common_Disabled")
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}