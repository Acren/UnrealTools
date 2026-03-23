using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class FlagOptions : OperationOptions
    {
        public override int SortIndex => 40;
        public override string Name => "Flags";

        public Option<bool> StompMalloc { get; } = false;

        public Option<bool> WaitForAttach { get; } = false;
    }
}
