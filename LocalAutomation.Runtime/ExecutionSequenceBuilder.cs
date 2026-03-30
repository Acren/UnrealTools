using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides a fluent sequence authoring surface where each declared step implicitly depends on the previous one.
/// </summary>
public sealed class ExecutionSequenceBuilder
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionGroupHandle _parent;
    private ExecutionStepHandle _lastStep;

    internal ExecutionSequenceBuilder(ExecutionPlanBuilder owner, ExecutionGroupHandle parent)
    {
        _owner = owner;
        _parent = parent;
    }

    /// <summary>
    /// Gets the most recently declared step in the sequence.
    /// </summary>
    public ExecutionStepHandle LastStep => _lastStep;

    /// <summary>
    /// Starts a new step in the sequence. The step automatically depends on the previously declared step when one
    /// exists.
    /// </summary>
    public ExecutionSequentialStepBuilder Step(string title, string? description = null)
    {
        ExecutionStepBuilder step = _owner.Step(title, description, _parent);
        if (_lastStep.IsValid)
        {
            step.After(_lastStep);
        }

        _lastStep = step.Handle;

        return new ExecutionSequentialStepBuilder(this, step);
    }

    /// <summary>
    /// Starts a new step in the sequence with an explicit stable identifier.
    /// </summary>
    public ExecutionSequentialStepBuilder Step(ExecutionTaskId id, string title, string? description = null)
    {
        ExecutionStepBuilder step = _owner.Step(id, title, description, _parent);
        if (_lastStep.IsValid)
        {
            step.After(_lastStep);
        }

        _lastStep = step.Handle;

        return new ExecutionSequentialStepBuilder(this, step);
    }

    internal void SetLastStep(ExecutionStepHandle handle)
    {
        _lastStep = handle;
    }
}
