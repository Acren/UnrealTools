namespace LocalAutomation.Core;

/// <summary>
/// Provides small helpers for consistently building displayable command lines from executable paths and argument
/// strings.
/// </summary>
public static class CommandLineFormatting
{
    /// <summary>
    /// Formats a command as an executable path followed by its argument string.
    /// </summary>
    public static string FormatCommand(string file, string arguments)
    {
        return $"\"{file}\" {arguments}";
    }

    /// <summary>
    /// Appends a new argument token onto an existing command line, inserting a separating space when needed.
    /// </summary>
    public static void CombineArgs(ref string original, string newArg)
    {
        if (!string.IsNullOrEmpty(original))
        {
            original += " ";
        }

        original += newArg;
    }

    /// <summary>
    /// Appends a dash-prefixed flag onto an existing command line.
    /// </summary>
    public static void AddFlag(ref string args, string flag)
    {
        CombineArgs(ref args, "-" + flag);
    }

    /// <summary>
    /// Appends a dash-prefixed key/value argument onto an existing command line.
    /// </summary>
    public static void AddValue(ref string args, string key, string value)
    {
        CombineArgs(ref args, "-" + key + "=" + value);
    }
}
