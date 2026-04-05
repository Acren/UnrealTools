using LocalAutomation.Core;

namespace UnrealAutomationCommon
{
    /// <summary>
    /// Keeps the existing UnrealAutomationCommon command helper entry points while delegating their implementation to
    /// LocalAutomation.Core.
    /// </summary>
    public class CommandUtils
    {
        /// <summary>
        /// Formats a command line for display using the shared LocalAutomation helper.
        /// </summary>
        public static string FormatCommand(string File, string Args)
        {
            return CommandLineFormatting.FormatCommand(File, Args);
        }

        /// <summary>
        /// Appends a new argument token onto an existing command string.
        /// </summary>
        public static void CombineArgs(ref string Original, string NewArg)
        {
            CommandLineFormatting.CombineArgs(ref Original, NewArg);
        }

        /// <summary>
        /// Appends a dash-prefixed flag onto an existing command string.
        /// </summary>
        public static void AddFlag(ref string Args, string Flag)
        {
            CommandLineFormatting.AddFlag(ref Args, Flag);
        }

        /// <summary>
        /// Appends a dash-prefixed key/value pair onto an existing command string.
        /// </summary>
        public static void AddValue(ref string Args, string Key, string Value)
        {
            CommandLineFormatting.AddValue(ref Args, Key, Value);
        }
    }
}
