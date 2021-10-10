using System.Collections.Generic;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander.Options
{
    public class AllowedBuildConfigurations
    {
        public List<BuildConfiguration> Configurations { get; set; } = new();
    }

    /// <summary>
    ///     Interaction logic for BuildConfigurationOptionsControl.xaml
    /// </summary>
    public partial class BuildConfigurationOptionsControl : OptionsUserControl
    {
        public BuildConfigurationOptionsControl()
        {
            DataContextChanged += (sender, args) =>
            {
                if (DataContext == null) return;
            };
            InitializeComponent();
        }

        public List<BuildConfiguration> BuildConfigurations => EnumUtils.GetAll<BuildConfiguration>();
    }
}