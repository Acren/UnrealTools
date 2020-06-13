using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon
{
    public class Command
    {
        public Command(string file, string arguments)
        {
            File = file;
            Arguments = arguments;
        }
        public string File { get; set; }
        public string Arguments { get; set; }

        public override string ToString()
        {
            return CommandUtils.FormatCommand(File, Arguments);
        }
    }
}
