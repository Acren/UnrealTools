using global::LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class CookOptions : global::LocalAutomation.Runtime.OperationOptions
    {
        private BuildConfiguration _cookerConfiguration = BuildConfiguration.Development;

        public override int SortIndex => 30;
        public override string Name => "Cooker";

        public BuildConfiguration CookerConfiguration
        {
            get => _cookerConfiguration;
            set
            {
                _cookerConfiguration = value;
                OnPropertyChanged();
            }
        }

        public global::LocalAutomation.Runtime.Option<bool> WaitForAttach { get; } = false;
    }
}
