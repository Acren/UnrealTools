using System.Collections.Generic;
using System.Windows;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander.Options
{
    public class AllowedBuildConfigurations
    {
        public List<BuildConfiguration> Configurations { get; set; } = new List<BuildConfiguration>();
    }

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

        public static readonly DependencyProperty AllowedBuildConfigurationsProperty = DependencyProperty.Register(nameof(AllowedBuildConfigurations), typeof(AllowedBuildConfigurations), typeof(BuildConfigurationOptionsControl));

        public AllowedBuildConfigurations AllowedBuildConfigurations
        {
            get => (AllowedBuildConfigurations)GetValue(AllowedBuildConfigurationsProperty);
            set => SetValue(AllowedBuildConfigurationsProperty, value);
        }

        public BuildConfigurationOptionsControl()
        {
            InitializeComponent();
        }

        public List<BuildConfiguration> BuildConfigurations => EnumUtils.GetAll<BuildConfiguration>();

    }
}
