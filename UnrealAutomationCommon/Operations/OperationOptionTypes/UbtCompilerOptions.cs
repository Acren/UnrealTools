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

    public class UbtCompilerOptions : OperationOptions
    {
        private UbtCompiler _compiler = UbtCompiler.Default;
        private UbtCppStandard _cppStandard = UbtCppStandard.Default;

        public override int SortIndex => 30;
        public override string Name => "Compiler";

        // Store the direct UBT overrides separately from general build options so unsupported UAT-based
        // operations do not accidentally advertise settings they cannot honor.
        public UbtCompiler Compiler
        {
            get => _compiler;
            set
            {
                _compiler = value;
                OnPropertyChanged();
            }
        }

        // Keep the direct UBT language-standard override on the same option set as compiler because both
        // settings apply to the exact same Build.bat-only execution path.
        public UbtCppStandard CppStandard
        {
            get => _cppStandard;
            set
            {
                _cppStandard = value;
                OnPropertyChanged();
            }
        }
    }
}
