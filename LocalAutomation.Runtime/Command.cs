using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents a fully constructed command line that can be previewed or executed by a host runtime.
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
}
