using System;
using System.Collections.Generic;
using System.Linq;

namespace UnrealAutomationCommon
{
    public static class EnumUtils
    {
        public static string GetName(object value)
        {
            return Enum.GetName(value.GetType(), value);
        }

        public static List<T> GetAll<T>() where T : Enum
        {
            return Enum.GetValues(typeof(T)).Cast<T>().ToList();
        }
    }
}