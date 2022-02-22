using System.Collections.Generic;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander.Options
{

    /// <summary>
    ///     Interaction logic for EngineVersionOptionsControl.xaml
    /// </summary>
    public partial class EngineVersionOptionsControl : OptionsUserControl
    {
        public EngineVersionOptionsControl()
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

        public List<BuildConfiguration> BuildConfigurations => EnumUtils.GetAll<BuildConfiguration>();
    }
}