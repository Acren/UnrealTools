using System.Collections.Generic;
using UnrealAutomationCommon.Unreal;
using LocalAutomation.Runtime;


namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class EngineVersionOptions : OperationOptions
    {
        public override int SortIndex => 10;

        public Option<List<EngineVersion>> EnabledVersions { get; } = new(new());
    }
}
