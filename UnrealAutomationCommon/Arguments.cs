using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon
{
    public class Arguments
    {
        string _argString;

        // args {argument}
        public void AddArgument(string argument)
        {
            CommandUtils.CombineArgs(ref _argString, argument);
        }

        // args -{flag}
        public void AddFlag(string flag)
        {
            CommandUtils.AddFlag(ref _argString, flag);
        }

        // args -{key}={value}
        public void AddKeyValue(string key, string value, bool wrapQuotes = false)
        {
            if (wrapQuotes)
            {
                value = "\"" + value + "\"";
            }
            CommandUtils.AddValue(ref _argString, key, value);
        }

        // args "{path}"
        public void AddPath(string path)
        {
            AddArgument("\"" + path + "\"");
        }

        // args -{key}="{path}"
        public void AddKeyPath(string key, string path)
        {
            AddKeyValue(key, path, true);
        }

        public override string ToString()
        {
            return _argString;
        }
    }
}
