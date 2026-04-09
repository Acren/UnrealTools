using System;
using System.Collections.Generic;
using System.Linq;

namespace UnrealAutomationCommon
{
    public static class EnumUtils
    {
        public static string GetName(object value)
        {
            /* Enum.GetName can return null for values outside the declared enum range, so fall back to the runtime
               value string to keep callers logging something meaningful instead of propagating nulls. */
            return Enum.GetName(value.GetType(), value) ?? value.ToString() ?? string.Empty;
        }

        public static List<T> GetAll<T>() where T : Enum
        {
            return Enum.GetValues(typeof(T)).Cast<T>().ToList();
        }
    }
}
