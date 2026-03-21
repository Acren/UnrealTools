using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents a fully constructed command line that can be previewed or executed by a host runtime.
/// </summary>
public class Command
{
    /// <summary>
    /// Creates a command from a file path and raw argument string.
    /// </summary>
    public Command(string file, string arguments)
    {
        File = file;
        Arguments = arguments;
    }

    /// <summary>
    /// Gets or sets the executable or script path to run.
    /// </summary>
    public string File { get; set; }

    /// <summary>
    /// Gets or sets the fully composed argument string for the command.
    /// </summary>
    public string Arguments { get; set; }

    /// <summary>
    /// Formats the command for display in logs, previews, and diagnostics.
    /// </summary>
    public override string ToString()
    {
        return CommandLineFormatting.FormatCommand(File, Arguments);
    }
}
