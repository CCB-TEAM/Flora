using System;
using System.Globalization;
using System.Windows.Data;

namespace FModel.Views.BlueprintViewer;

/// <summary>Header rows of the widget property editor span every column.</summary>
public class HeaderSpanConverter : IValueConverter
{
    public static readonly HeaderSpanConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? 3 : 1;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
