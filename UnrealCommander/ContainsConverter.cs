using System;
using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace UnrealCommander
{
    public class ContainsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2)
            {
                return false;
            }

            IList collection = (IList)values[0];

            if (collection == null)
            {
                return false;
            }

            return collection.Contains(values[1]);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
