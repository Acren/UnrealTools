using System;
using System.Windows.Data;
using UnrealAutomationCommon.Operations;

namespace UnrealCommander
{
    public class OperationSupportedConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (!(values[0] is Type))
            {
                return false;
            }

            Type operationType = values[0] as Type;

            if (values[1] is OperationTarget target && !Operation.OperationTypeSupportsTarget(operationType, target))
            {
                return false;
            }

            return true;
        }

        // No need to implement converting back on a one-way binding 
        public object[] ConvertBack(object value, Type[] targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
