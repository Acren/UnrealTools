using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public class OperationResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public TestReport TestReport { get; set; }

        public OperationResult(bool success)
        {
            Success = success;
        }
    }
}
