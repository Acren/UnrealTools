using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public class OperationResult
    {
        public OperationResult(bool success)
        {
            Success = success;
        }

        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public TestReport TestReport { get; set; }
    }
}