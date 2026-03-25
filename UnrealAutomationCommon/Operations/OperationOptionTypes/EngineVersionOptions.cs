using System.ComponentModel;
using UnrealAutomationCommon.Unreal;
using LocalAutomation.Runtime;


namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class EngineVersionOptions : OperationOptions
    {
        /// <summary>
        /// Creates a stable collection-backed engine selection model so checklist edits can raise change notifications
        /// immediately instead of relying on replacing an entire wrapped list value.
        /// </summary>
        public EngineVersionOptions()
        {
            EnabledVersions.ListChanged += HandleEnabledVersionsChanged;
            EnabledVersions.RaiseListChangedEvents = true;
        }

        public override int SortIndex => 10;

        /// <summary>
        /// Gets the explicitly selected engine versions. Most operations use zero or one explicit selection and then
        /// fall back to the target's engine when the list is empty.
        /// </summary>
        public BindingList<EngineVersion> EnabledVersions { get; } = new();

        /// <summary>
        /// Bubbles collection edits up through the containing option set so command preview and validation refresh as
        /// soon as the user changes engine selection.
        /// </summary>
        private void HandleEnabledVersionsChanged(object? sender, ListChangedEventArgs e)
        {
            OnPropertyChanged(nameof(EnabledVersions));
        }
    }
}
