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
                case BuildConfiguration.DebugGame:
                    return "DebugGame";
                case BuildConfiguration.Development:
                    return "Development";
                case BuildConfiguration.Shipping:
                    return "Shipping";
            }
            return "Invalid";
        }
    }

    public enum BuildConfiguration
    {
        DebugGame,
        Development,
        Shipping
    }
}
