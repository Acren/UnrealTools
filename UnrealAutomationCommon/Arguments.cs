using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnrealAutomationCommon
{
    public class Argument
    {
        public Argument()
        {
        }

        // Parse argument from a string
        public Argument(string argString)
        {
            bool inQuote = false;
            bool inValue = false;
            for (int i = 0; i < argString.Length; i++)
            {
                char character = argString[i];
                if (i == 0 && character == '-')
                {
                    DashPrefix = true;
                    continue;
                }

                if (character == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (!inQuote && !inValue && character == '=')
                {
                    inValue = true;
                    continue;
                }

                if (inValue)
                {
                    Value += character;
                }
                else
                {
                    Key += character;
                }

            }
        }

        public string Key { get; set; }
        public string Value { get; set; }
        public bool DashPrefix { get; set; }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Key))
            {
                throw new Exception("Empty key");
            }

            var argString = "";
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
        private readonly List<Argument> _arguments = new();
        private string _rawArgs = "";

        public Arguments()
        {
        }

        public Arguments(IEnumerable<string> argStrings)
        {
            foreach(string argString in argStrings)
            {
                Argument parsedArgument = new Argument(argString);

                if (!string.IsNullOrEmpty(parsedArgument.Key))
                {
                    UpdateArgument(parsedArgument, true);
                }
            }
        }

        private void UpdateArgument(Argument argument, bool updateExisting)
        {
            if (String.IsNullOrEmpty(argument.Key))
            {
                throw new Exception("Invalid key");
            }

            Argument existingArgument = _arguments.SingleOrDefault(a => a.Key.Equals(argument.Key, StringComparison.InvariantCultureIgnoreCase));

            if (existingArgument != null)
            {
                if (updateExisting)
                {
                    // Replace existing argument at the same index
                    int index = _arguments.IndexOf(existingArgument);
                    _arguments.RemoveAt(index);
                    _arguments.Insert(index, argument);
                }
            }
            else
            {
                _arguments.Add(argument);
            }
        }

        public Argument GetArgument(string key)
        {
            return _arguments.Find(a => a.Key.Equals(key, StringComparison.InvariantCultureIgnoreCase));
        }

        public bool HasArgument(string key)
        {
            return GetArgument(key) != null;
        }

        // args {argument}
        public void SetArgument(string argument, bool updateExisting = true)
        {
            UpdateArgument(new Argument
            {
                Key = argument
            }, updateExisting);
        }

        // args -{flag}
        public void SetFlag(string flag, bool updateExisting = true)
        {
            UpdateArgument(new Argument
            {
                Key = flag,
                DashPrefix = true
            }, updateExisting);
        }

        // args -{key}={value}
        public void SetKeyValue(string key, string value, bool updateExisting = true)
        {
            UpdateArgument(new Argument
            {
                Key = key,
                Value = value,
                DashPrefix = true
            }, updateExisting);
        }

        // args "{path}"
        public void SetPath(string path, bool updateExisting = true)
        {
            UpdateArgument(new Argument
            {
                Key = path
            }, updateExisting);
        }

        // args -{key}="{path}"
        public void SetKeyPath(string key, string path, bool updateExisting = true)
        {
            UpdateArgument(new Argument
            {
                Key = key,
                Value = path,
                DashPrefix = true
            }, updateExisting);
        }

        public void AddRawArgsString(string rawArgsString)
        {
            foreach (string argString in CommandLineParser.SplitCommandLineIntoArguments(rawArgsString, false))
            {
                Argument parsedArgument = new Argument(argString);

                if (!string.IsNullOrEmpty(parsedArgument.Key))
                {
                    UpdateArgument(parsedArgument, true);
                }
            }
        }

        public override string ToString()
        {
            var builtCommandString = "";
            foreach (Argument arg in _arguments)
            {
                CommandUtils.CombineArgs(ref builtCommandString, arg.ToString());
            }

            CommandUtils.CombineArgs(ref builtCommandString, _rawArgs);
            return builtCommandString;
        }

    }
}