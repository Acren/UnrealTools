using System.Collections.Generic;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealCommander.Options
{
    /// <summary>
    ///     Interaction logic for UbtCompilerOptionsControl.xaml
    /// </summary>
    public partial class UbtCompilerOptionsControl : OptionsUserControl
    {
        public UbtCompilerOptionsControl()
        {
            InitializeComponent();
        }

        // Surface the available compiler overrides from the shared enum so the UI and command builder stay aligned.
        public List<UbtCompiler> Compilers => EnumUtils.GetAll<UbtCompiler>();

        // Surface the available language-standard overrides from the shared enum so the UI and command builder stay aligned.
        public List<UbtCppStandard> CppStandards => EnumUtils.GetAll<UbtCppStandard>();
    }
}
