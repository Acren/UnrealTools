using System.Collections.Generic;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class EngineVersionOptions : OperationOptions
    {
        public override int Index => 10;

        public Option<List<EngineInstallVersion>> EnabledVersions { get; } = new(new());
    }
}