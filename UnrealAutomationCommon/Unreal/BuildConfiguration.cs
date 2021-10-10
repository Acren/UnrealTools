using System;
using System.Collections.Generic;
using System.Linq;

namespace UnrealAutomationCommon.Unreal
{
    public static class Extensions
    {
        public static BuildConfiguration Hello;

        public static string ToString(this BuildConfiguration config)
        {
            try
            {
                string EnumString = Enum.GetName(config.GetType(), config);
                return EnumString;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static List<BuildConfiguration> GetAll()
        {
            return Enum.GetValues(typeof(BuildConfiguration)).Cast<BuildConfiguration>().ToList();
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