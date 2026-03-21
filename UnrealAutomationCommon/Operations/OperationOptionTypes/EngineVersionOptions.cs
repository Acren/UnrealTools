using System.Collections.Generic;
using global::LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class EngineVersionOptions : global::LocalAutomation.Runtime.OperationOptions
    {
        public override int SortIndex => 10;

        public global::LocalAutomation.Runtime.Option<List<EngineVersion>> EnabledVersions { get; } = new(new());
    }
}
