using LocalAutomation.Core;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Extends the shared LocalAutomation run result with Unreal-specific test report data used by existing
    /// operations.
    /// </summary>
    public class OperationResult : RunResult
    {
        /// <summary>
        /// Creates an operation result with the provided success state.
        /// </summary>
        public OperationResult(bool success)
            : base(success)
        {
        }

        /// <summary>
        /// Gets or sets the Unreal test report collected during the run when one is available.
        /// </summary>
        public TestReport TestReport { get; set; }
    }
}
