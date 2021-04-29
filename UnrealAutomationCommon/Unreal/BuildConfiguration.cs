using System;

namespace UnrealAutomationCommon.Unreal
{
    public static class Extensions
    {
        public static string ToString(this BuildConfiguration config)
        {
            try
            {
                string EnumString = Enum.GetName((config.GetType()), config);
                return EnumString;
            }
            catch
            {
                return string.Empty;
            }
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
