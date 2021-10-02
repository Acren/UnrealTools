using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginDeployOptions : OperationOptions
    {
        public Option<string> ArchivePath { get; }

        public PluginDeployOptions()
        {
            ArchivePath = new Option<string>(OptionChanged, null);
        }
    }
}
