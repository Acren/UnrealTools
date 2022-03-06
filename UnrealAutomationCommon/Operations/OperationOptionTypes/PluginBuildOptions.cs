﻿namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginBuildOptions : OperationOptions
    {
        public override int Index => 45;

        public Option<bool> BuildWin64 { get; } = true;
        public Option<bool> BuildLinux { get; } = true;
        public Option<bool> StrictIncludes { get; } = true;
    }
}