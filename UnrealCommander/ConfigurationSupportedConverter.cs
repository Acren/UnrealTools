using System;
using System.Windows.Data;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander
{
    public class ConfigurationSupportedConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (!(values[0] is BuildConfiguration))
            {
                return false;
            }

            BuildConfiguration buildConfiguration = (BuildConfiguration) values[0];

            if (values[1] is EngineInstall engineInstall && !engineInstall.SupportsConfiguration(buildConfiguration))
            {
                return false;
            }

            if (values[2] is Operation operation && !operation.SupportsConfiguration(buildConfiguration))
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
