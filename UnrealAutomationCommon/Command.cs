using LocalAutomation.Core;

namespace UnrealAutomationCommon
{
    /// <summary>
    /// Preserves the existing UnrealAutomationCommon command API while delegating the shared runtime model to
    /// LocalAutomation.Core.
    /// </summary>
    public class Command : CommandSpec
    {
        /// <summary>
        /// Creates a command from a file path and raw argument string.
        /// </summary>
        public Command(string file, string arguments)
            : base(file, arguments)
        {
        }

        /// <summary>
        /// Creates a command from a file path and an UnrealAutomationCommon argument builder.
        /// </summary>
        public Command(string file, Arguments arguments)
            : base(file, arguments.ToString())
        {
        }
    }
}
