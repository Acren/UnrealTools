using System.Collections.Generic;
using global::LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class EngineVersionOptions : OperationOptions
    {
        public override int SortIndex => 10;

        public Option<List<EngineVersion>> EnabledVersions { get; } = new(new());
    }
}
