using System.Collections.Generic;
using System.Windows;
using UnrealAutomationCommon;
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
        //public static readonly DependencyProperty AllowedBuildConfigurationsProperty = DependencyProperty.Register(nameof(AllowedBuildConfigurations), typeof(AllowedBuildConfigurations), typeof(BuildConfigurationOptionsControl), new FrameworkPropertyMetadata(new AllowedBuildConfigurations()));

        //public AllowedBuildConfigurations AllowedBuildConfigurations
        //{
        //    get => (AllowedBuildConfigurations)GetValue(AllowedBuildConfigurationsProperty);
        //    set => SetValue(AllowedBuildConfigurationsProperty, value);
        //}

        public BuildConfigurationOptionsControl()
        {
            DataContextChanged += (sender, args) =>
            {
                if (this.DataContext == null)
                {
                    return;
                }
            };
            InitializeComponent();
        }

        public List<BuildConfiguration> BuildConfigurations => EnumUtils.GetAll<BuildConfiguration>();

    }
}
