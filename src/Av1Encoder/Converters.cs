using System.Globalization;
using System.Windows.Data;

namespace Av1Encoder;

public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true ? Binding.DoNothing
        : targetType.IsEnum ? Enum.Parse(targetType, (string)parameter)
        : int.Parse((string)parameter, CultureInfo.InvariantCulture);
}
