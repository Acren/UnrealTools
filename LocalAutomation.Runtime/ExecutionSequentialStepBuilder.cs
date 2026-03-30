using System;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Wraps a step declaration inside a fluent sequence so authoring can continue naturally after Then/Declare.
/// </summary>
public sealed class ExecutionSequentialStepBuilder
{
    private readonly ExecutionSequenceBuilder _sequence;
    private readonly ExecutionStepBuilder _step;

    internal ExecutionSequentialStepBuilder(ExecutionSequenceBuilder sequence, ExecutionStepBuilder step)
    {
        _sequence = sequence;
        _step = step;
    }

    /// <summary>
    /// Marks whether the current step should participate in the plan.
    /// </summary>
    public ExecutionSequentialStepBuilder When(bool enabled, string? disabledReason = null)
    {
        _step.When(enabled, disabledReason);
        return this;
    }

    /// <summary>
    /// Overrides the step description without forcing long text into the Step(...) call.
    /// </summary>
    public ExecutionSequentialStepBuilder Describe(string? description)
    {
        _step.Describe(description);
        return this;
    }

    /// <summary>
    /// Adds an explicit dependency in addition to the implicit sequential dependency.
    /// </summary>
    public ExecutionSequentialStepBuilder After(ExecutionStepHandle dependency)
    {
        _step.After(dependency);
        return this;
    }

    /// <summary>
    /// Attaches a context-aware callback and continues the fluent sequence.
    /// </summary>
    public ExecutionSequenceBuilder Then(Func<ExecutionTaskContext, Task> executeAsync)
    {
        _sequence.SetLastStep(_step.Then(executeAsync));
        return _sequence;
    }

    /// <summary>
    /// Attaches a parameterless callback and continues the fluent sequence.
    /// </summary>
    public ExecutionSequenceBuilder Then(Func<Task> executeAsync)
    {
        _sequence.SetLastStep(_step.Then(executeAsync));
        return _sequence;
    }

    /// <summary>
    /// Starts the next step in the sequence while keeping the current step as a preview-only declaration.
    /// </summary>
    public ExecutionSequentialStepBuilder Step(string title, string? description = null)
    {
        return _sequence.Step(title, description);
    }

    /// <summary>
    /// Starts the next step in the sequence with an explicit stable identifier.
    /// </summary>
    public ExecutionSequentialStepBuilder Step(ExecutionTaskId id, string title, string? description = null)
    {
        return _sequence.Step(id, title, description);
    }
}
