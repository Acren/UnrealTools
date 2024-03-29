﻿using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class BuildConfigurationOptions : OperationOptions
    {
        private BuildConfiguration _configuration = BuildConfiguration.Development;

        public override int SortIndex => 20;

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