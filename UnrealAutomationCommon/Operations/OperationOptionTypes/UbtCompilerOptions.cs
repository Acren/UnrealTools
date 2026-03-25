using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    // Limit compiler selection to the small set of direct UBT flows that reliably honor it.
    public enum UbtCompiler
    {
        Default,
        Clang
    }

    // Limit C++ standard overrides to direct UBT flows that forward flags straight through to UnrealBuildTool.
    public enum UbtCppStandard
    {
        Default,
        Cpp17,
        Cpp20
    }

    public partial class UbtCompilerOptions : OperationOptions
    {
        public override int SortIndex => 30;

        public override string Name => "Compiler";

        // Store the direct UBT overrides separately from general build options so unsupported UAT-based
        // operations do not accidentally advertise settings they cannot honor.
        [ObservableProperty]
        private UbtCompiler compiler = UbtCompiler.Default;

        // Keep the direct UBT language-standard override on the same option set as compiler because both
        // settings apply to the exact same Build.bat-only execution path.
        [ObservableProperty]
        private UbtCppStandard cppStandard = UbtCppStandard.Default;
    }
}
