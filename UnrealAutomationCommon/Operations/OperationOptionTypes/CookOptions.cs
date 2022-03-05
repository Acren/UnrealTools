using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class CookOptions : OperationOptions
    {
        private BuildConfiguration _cookerConfiguration = BuildConfiguration.Development;

        public override int Index => 30;

        public BuildConfiguration CookerConfiguration
        {
            get => _cookerConfiguration;
            set
            {
                _cookerConfiguration = value;
                OnPropertyChanged();
            }
        }
    }
}
