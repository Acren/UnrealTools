using System;
using System.Collections.Generic;
using System.Linq;
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

        private void UpdateArgument(Argument argument)
        {
            Argument existingArgument = _arguments.SingleOrDefault(a => a.Key.Equals(argument.Key, StringComparison.InvariantCulture));

            if (existingArgument != null)
            {
                // Replace existing argument at the same index
                int index = _arguments.IndexOf(existingArgument);
                _arguments.RemoveAt(index);
                _arguments.Insert(index, argument);
            }
            else
            {
                _arguments.Add(argument);
            }

        }

        // args {argument}
        public void SetArgument(string argument)
        {
            UpdateArgument(new Argument()
            {
                Key = argument
            });
        }

        // args -{flag}
        public void SetFlag(string flag)
        {
            UpdateArgument(new Argument()
            {
                Key = flag,
                DashPrefix = true
            });
        }

        // args -{key}={value}
        public void SetKeyValue(string key, string value, bool wrapQuotes = false)
        {
            UpdateArgument(new Argument()
            {
                Key = key,
                Value = value,
                DashPrefix = true
            });
        }

        // args "{path}"
        public void SetPath(string path)
        {
            UpdateArgument(new Argument()
            {
                Key = path
            });
        }

        // args -{key}="{path}"
        public void SetKeyPath(string key, string path)
        {
            UpdateArgument(new Argument()
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
