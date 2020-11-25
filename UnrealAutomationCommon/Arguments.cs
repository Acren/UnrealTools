using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon
{
    public class Arguments
    {
        string _argString;

        public void AddArgument(string argument)
        {
            CommandUtils.CombineArgs(ref _argString, argument);
        }

        public void AddFlag(string flag)
        {
            CommandUtils.AddFlag(ref _argString, flag);
        }

        public void AddKeyValue(string key, string value)
        {
            CommandUtils.AddValue(ref _argString, key, value);
        }

        public void AddPath(string path)
        {
            AddArgument("\"" + path + "\"");
        }

        public void AddKeyPath(string key, string path)
        {
            AddKeyValue(key, "\"" + path + "\"");
        }

        public override string ToString()
        {
            return _argString;
        }
    }
}
