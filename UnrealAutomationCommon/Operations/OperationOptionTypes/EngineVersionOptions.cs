using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OptionChoiceSources;
using UnrealAutomationCommon.Unreal;


namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class EngineVersionOptions : OperationOptions
    {
        public override int SortIndex => 10;

        /// <summary>
        /// Gets the explicitly selected engine versions. Most operations use zero or one explicit selection and then
        /// fall back to the target's engine when the list is empty.
        /// </summary>
        [ObservableProperty]
        [property: DisplayName("Enabled Versions")]
        [property: Description("Selects which installed engine versions an operation should target; leaving the list empty falls back to the target's resolved engine.")]
        [property: ChoiceCollectionSource(typeof(InstalledEngineVersionChoiceSource))]
        private IReadOnlyList<EngineVersion> enabledVersions = Array.Empty<EngineVersion>();
    }
}
