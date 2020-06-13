using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon
{
    class Arguments
    {
        string argString;

        public void AddAction(string Action)
        {
            CommandUtils.CombineArgs(ref argString, Action);
        }

        public void AddFlag(string Flag)
        {
            CommandUtils.AddFlag(ref argString, Flag);
        }

        public void AddValue(string Key, string Value)
        {
            CommandUtils.AddValue(ref argString, Key, Value);
        }

        public void AddPath(string Key, string Path)
        {
            AddValue(Key, "\"" + Path + "\"");
        }

        public override string ToString()
        {
            return argString;
        }
    }
}
