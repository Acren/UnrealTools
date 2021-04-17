using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Data;
using System.Windows.Markup;
using UnrealAutomationCommon;

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

            BuildConfiguration Config = (BuildConfiguration) values[0];

            if (values[1] is EngineInstall EngineInstall)
            {
                return EngineInstall.SupportsConfiguration(Config);
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
