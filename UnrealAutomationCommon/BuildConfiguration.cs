using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon
{
    public static class Extensions
    {
        public static string ToString(this BuildConfiguration config)
        {
            switch(config)
            {
                case BuildConfiguration.Debug:
                    return "Debug";
                case BuildConfiguration.DebugGame:
                    return "DebugGame";
                case BuildConfiguration.Development:
                    return "Development";
                case BuildConfiguration.Test:
                    return "Test";
                case BuildConfiguration.Shipping:
                    return "Shipping";
            }
            return "Invalid";
        }
    }

    public enum BuildConfiguration
    {
        Debug,
        DebugGame,
        Development,
        Test,
        Shipping
    }
}
