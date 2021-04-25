using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon
{
    public class Argument
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public bool DashPrefix { get; set; }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Key))
            {
                throw new Exception("Empty key");
            }

            string argString = "";
            if (DashPrefix)
            {
                argString += "-";
            }

            argString += Key.AddQuotesIfContainsSpace();

            if (!string.IsNullOrEmpty(Value))
            {
                argString += "=";
                argString += Value.AddQuotesIfContainsSpace();
            }

            return argString;
        }
    }

    public class Arguments
    {
        private readonly List<Argument> _arguments = new List<Argument>();

        // args {argument}
        public void AddArgument(string argument)
        {
            _arguments.Add(new Argument()
            {
                Key = argument
            });
        }

        // args -{flag}
        public void AddFlag(string flag)
        {
            _arguments.Add(new Argument()
            {
                Key = flag,
                DashPrefix = true
            });
        }

        // args -{key}={value}
        public void AddKeyValue(string key, string value, bool wrapQuotes = false)
        {
            _arguments.Add(new Argument()
            {
                Key = key,
                Value = value,
                DashPrefix = true
            });
        }

        // args "{path}"
        public void AddPath(string path)
        {
            _arguments.Add(new Argument()
            {
                Key = path
            });
        }

        // args -{key}="{path}"
        public void AddKeyPath(string key, string path)
        {
            _arguments.Add(new Argument()
            {
                Key = key,
                Value = path,
                DashPrefix = true
            });
        }

        public override string ToString()
        {
            string builtCommandString = "";
            foreach (Argument arg in _arguments)
            {
                CommandUtils.CombineArgs(ref builtCommandString, arg.ToString());
            }
            return builtCommandString;
        }
    }
}
