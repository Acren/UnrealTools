using System.Collections.Generic;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class BuildConfigurationOptions : OperationOptions
    {
        private BuildConfiguration _configuration;

        public BuildConfiguration Configuration
        {
            get => _configuration;
            set
            {
                _configuration = value;
                OnPropertyChanged();
            }
        }
    }
}
