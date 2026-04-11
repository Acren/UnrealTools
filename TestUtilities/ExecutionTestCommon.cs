using System;
using LocalAutomation.Runtime;

namespace TestUtilities;

/// <summary>
/// Shared execution-test helpers that stay generic across multiple test projects.
/// Scenario-specific helpers should remain local to their owning test assembly.
/// </summary>
internal static class ExecutionTestCommon
{
    /// <summary>
    /// Minimal lock-free inline operation wrapper that lets tests define plan shape through the normal runtime operation
    /// pipeline. Tests that need lock contention should declare locks on individual authored tasks instead of applying
    /// one blanket lock policy to the whole synthetic operation.
    /// </summary>
    internal class InlineOperation : Operation<TestTarget>
    {
        private readonly Action<ExecutionTaskBuilder>? _buildPlan;
        private readonly string _operationName;
        private readonly Func<ValidatedOperationParameters, string?>? _checkRequirements;

        public InlineOperation(
            Action<ExecutionTaskBuilder>? buildPlan = null,
            string operationName = "Test Operation",
            Func<ValidatedOperationParameters, string?>? checkRequirements = null)
        {
            _buildPlan = buildPlan;
            _operationName = string.IsNullOrWhiteSpace(operationName) ? "Test Operation" : operationName;
            _checkRequirements = checkRequirements;
        }

        /// <summary>
        /// Keeps operation naming deterministic while allowing each test assembly to provide a more specific label when
        /// that improves readability.
        /// </summary>
        protected override string GetOperationName()
        {
            return _operationName;
        }

        /// <summary>
        /// Delegates authored task construction to the test-provided builder action.
        /// </summary>
        protected override void DescribeExecutionPlan(ValidatedOperationParameters operationParameters, ExecutionTaskBuilder root)
        {
            _buildPlan?.Invoke(root);
        }

        /// <summary>
        /// Lets tests inject one synthetic requirements outcome without introducing a scenario-specific derived operation
        /// type for each execution-path assertion.
        /// </summary>
        protected override string? CheckRequirementsSatisfied(ValidatedOperationParameters operationParameters)
        {
            return _checkRequirements?.Invoke(operationParameters) ?? base.CheckRequirementsSatisfied(operationParameters);
        }
    }

    /// <summary>
    /// Minimal valid target used by execution-oriented tests when the pipeline requires a concrete operation target.
    /// </summary>
    internal sealed class TestTarget : OperationTarget
    {
        public TestTarget()
        {
            TargetPath = AppContext.BaseDirectory;
        }

        /// <summary>
        /// Uses one stable display name for deterministic task paths and session metadata in tests.
        /// </summary>
        public override string Name => "TestTarget";

        /// <summary>
        /// The shared test target has no descriptor-backed state to load.
        /// </summary>
        public override void LoadDescriptor()
        {
        }
    }
}
