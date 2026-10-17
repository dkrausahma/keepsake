using System;
using System.Globalization;
using System.Windows.Data;

namespace Keepsake.Gui.Infrastructure
{
    /// <summary>Passes several bound elements (e.g. two PasswordBoxes) as one command parameter.</summary>
    public sealed class ArrayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Clone();

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
