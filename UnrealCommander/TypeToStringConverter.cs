using System;
using System.Globalization;
using System.Windows.Data;
using UnrealAutomationCommon;

namespace UnrealCommander
{
    public class TypeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            Type type = value as Type;

            if (type == null)
            {
                return string.Empty;
            }

            return type.Name.SplitWordsByUppercase();
        }

        // No need to implement converting back on a one-way binding 
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}