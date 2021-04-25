using System;
using System.Linq;

namespace UnrealAutomationCommon
{
    public static class StringExtensions
    {
        public static string SplitWordsByUppercase(this string str)
        {
            return string.Concat(str.Select(x => Char.IsUpper(x) ? " " + x : x.ToString())).TrimStart(' ');
        }

        public static string AddQuotesIfContainsSpace(this string str)
        {
            if (str.Any(Char.IsWhiteSpace))
            {
                return "\"" + str + "\"";
            }

            return str;
        }
    }
}
