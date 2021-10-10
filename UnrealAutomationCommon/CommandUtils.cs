namespace UnrealAutomationCommon
{
    public class CommandUtils
    {
        public static string FormatCommand(string File, string Args)
        {
            return "\"" + File + "\" " + Args;
        }

        public static void CombineArgs(ref string Original, string NewArg)
        {
            if (!string.IsNullOrEmpty(Original))
            {
                Original += " ";
            }

            Original += NewArg;
        }

        public static void AddFlag(ref string Args, string Flag)
        {
            CombineArgs(ref Args, "-" + Flag);
        }

        public static void AddValue(ref string Args, string Key, string Value)
        {
            CombineArgs(ref Args, "-" + Key + "=" + Value);
        }
    }
}