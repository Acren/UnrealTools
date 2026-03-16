namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    // Limit compiler selection to the small set of direct UBT flows that reliably honor it.
    public enum UbtCompiler
    {
        Default,
        Clang
    }

    public class UbtCompilerOptions : OperationOptions
    {
        private UbtCompiler _compiler = UbtCompiler.Default;

        public override int SortIndex => 30;
        public override string Name => "Compiler";

        // Store the direct UBT compiler override separately from general build options so unsupported
        // UAT-based operations do not accidentally advertise a setting they cannot honor.
        public UbtCompiler Compiler
        {
            get => _compiler;
            set
            {
                _compiler = value;
                OnPropertyChanged();
            }
        }
    }
}
