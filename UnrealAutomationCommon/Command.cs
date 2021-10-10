namespace UnrealAutomationCommon
{
    public class Command
    {
        public Command(string file, string arguments)
        {
            File = file;
            Arguments = arguments;
        }

        public Command(string file, Arguments arguments)
        {
            File = file;
            Arguments = arguments.ToString();
        }

        public string File { get; set; }
        public string Arguments { get; set; }

        public override string ToString()
        {
            return CommandUtils.FormatCommand(File, Arguments);
        }
    }
}