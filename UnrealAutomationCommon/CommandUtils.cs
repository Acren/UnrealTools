using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon
{
    public class CommandUtils
    {
        public static string FormatCommand(string File, string Args)
        {
            return "\"" + File + "\" " + Args;
        }
    }
}
