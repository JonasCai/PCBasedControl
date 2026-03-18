using System;
using System.Globalization;
using System.Windows.Data;
using UI.ViewModels;

namespace UI.Converters;

public class BottomNavItemClickParameterConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return null;

        var group = values[0] as BottomNavGroupViewModel;
        var item = values[1] as BottomNavItemViewModel;

        if (group is null || item is null)
            return null;

        return new BottomNavItemClickParameter
        {
            Group = group,
            Item = item
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
