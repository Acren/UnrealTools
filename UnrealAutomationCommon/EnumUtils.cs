using System;

namespace UnrealAutomationCommon
{
    public static class EnumUtils
    {
        public static string GetName(object value)
        {
            return Enum.GetName(value.GetType(), value);
        }
    }
}
