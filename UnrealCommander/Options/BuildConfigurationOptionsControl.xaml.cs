using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for BuildConfigurationOptionsControl.xaml
    /// </summary>
    public partial class BuildConfigurationOptionsControl : OptionsUserControl
    {
        private BuildConfigurationOptions _options = null;

        public BuildConfigurationOptions Options
        {
            get => _options;
            set { _options = value; OnPropertyChanged(); }
        }

        public static readonly DependencyProperty AllowedBuildConfigurationsProperty = DependencyProperty.Register(nameof(AllowedBuildConfigurations), typeof(BindingList<BuildConfiguration>), typeof(BuildConfigurationOptionsControl));

        public BindingList<BuildConfiguration> AllowedBuildConfigurations
        {
            get => (BindingList<BuildConfiguration>)GetValue(AllowedBuildConfigurationsProperty);
            set => SetValue(AllowedBuildConfigurationsProperty, value);
        }

        public BuildConfigurationOptionsControl()
        {
            InitializeComponent();
        }

        public List<BuildConfiguration> BuildConfigurations => EnumUtils.GetAll<BuildConfiguration>();

    }
}
