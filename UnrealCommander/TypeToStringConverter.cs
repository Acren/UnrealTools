using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Data;

namespace UnrealCommander
{
    public class TypeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            Type type = value as Type;

            if (type == null)
            {
                return string.Empty;
            }

            return type.Name;
        }

        // No need to implement converting back on a one-way binding 
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
