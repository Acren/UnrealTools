using System.Linq;

namespace UnrealAutomationCommon
{
    public static class StringExtensions
    {
        public static string SplitWordsByUppercase(this string str)
        {
            return string.Concat(str.Select(x => char.IsUpper(x) ? " " + x : x.ToString())).TrimStart(' ');
        }

        public static string AddQuotesIfContainsSpace(this string str)
        {
            if (str.Any(char.IsWhiteSpace))
            {
                return "\"" + str + "\"";
            }

            return str;
        }
    }
}