using System.Collections.Generic;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander.Options
{
    /// <summary>
    ///     Interaction logic for BuildConfigurationOptionsControl.xaml
    /// </summary>
    public partial class CookOptionsControl : OptionsUserControl
    {
        public CookOptionsControl()
        {
            DataContextChanged += (sender, args) =>
            {
                if (DataContext == null)
                {
                    return;
                }
            };
            InitializeComponent();
        }

        public List<BuildConfiguration> CookerConfigurations => new() {BuildConfiguration.DebugGame, BuildConfiguration.Development};
    }
}