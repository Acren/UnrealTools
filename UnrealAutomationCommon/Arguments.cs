using System;
using System.Collections.Generic;
using System.Linq;

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
        private string _rawArgs = "";

        private void UpdateArgument(Argument argument, bool updateExisting)
        {
            Argument existingArgument = _arguments.SingleOrDefault(a => a.Key.Equals(argument.Key, StringComparison.InvariantCulture));

            if (existingArgument != null)
            {
                if (updateExisting)
                {
                    // Replace existing argument at the same index
                    int index = _arguments.IndexOf(existingArgument);
                    _arguments.RemoveAt(index);
                    _arguments.Insert(index, argument);
                }
                else
                {
                    // Do nothing
                }
            }
            else
            {
                _arguments.Add(argument);
            }
        }

        // args {argument}
        public void SetArgument(string argument, bool updateExisting = true)
        {
            UpdateArgument(new Argument()
            {
                Key = argument
            }, updateExisting);
        }

        // args -{flag}
        public void SetFlag(string flag, bool updateExisting = true)
        {
            UpdateArgument(new Argument()
            {
                Key = flag,
                DashPrefix = true
            }, updateExisting);
        }

        // args -{key}={value}
        public void SetKeyValue(string key, string value, bool updateExisting = true)
        {
            UpdateArgument(new Argument()
            {
                Key = key,
                Value = value,
                DashPrefix = true
            }, updateExisting);
        }

        // args "{path}"
        public void SetPath(string path, bool updateExisting = true)
        {
            UpdateArgument(new Argument()
            {
                Key = path
            }, updateExisting);
        }

        // args -{key}="{path}"
        public void SetKeyPath(string key, string path, bool updateExisting = true)
        {
            UpdateArgument(new Argument()
            {
                Key = key,
                Value = path,
                DashPrefix = true
            }, updateExisting);
        }

        public void AddRawArgsString(string args)
        {
            CommandUtils.CombineArgs(ref _rawArgs, args);
        }

        public override string ToString()
        {
            string builtCommandString = "";
            foreach (Argument arg in _arguments)
            {
                CommandUtils.CombineArgs(ref builtCommandString, arg.ToString());
            }
            CommandUtils.CombineArgs(ref builtCommandString, _rawArgs);
            return builtCommandString;
        }
    }
}
