using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginBuildOptions : OperationOptions
    {
        public Option<bool> StrictIncludes { get; }

        public PluginBuildOptions()
        {
            StrictIncludes = new Option<bool>(OptionChanged, false);
        }
    }
}
