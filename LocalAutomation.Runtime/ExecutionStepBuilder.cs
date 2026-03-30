using System;
using System.Threading.Tasks;

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides the fluent authoring surface for one declared step inside an execution plan.
/// </summary>
public sealed class ExecutionStepBuilder
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionPlanBuilder.PlanItemDefinition _definition;

    internal ExecutionStepBuilder(ExecutionPlanBuilder owner, ExecutionPlanBuilder.PlanItemDefinition definition)
    {
        _owner = owner;
        _definition = definition;
        Handle = _owner.FinalizeStep(_definition);
    }

    internal ExecutionStepHandle Handle { get; }

    /// <summary>
    /// Declares that this step depends on the provided earlier step.
    /// </summary>
    public ExecutionStepBuilder After(ExecutionStepHandle dependency)
    {
        _owner.AddDependency(_definition, dependency);
        return this;
    }

    /// <summary>
    /// Declares that this step depends on each provided earlier step.
    /// </summary>
    public ExecutionStepBuilder After(params ExecutionStepHandle[] dependencies)
    {
        foreach (ExecutionStepHandle dependency in dependencies)
        {
            _owner.AddDependency(_definition, dependency);
        }

        return this;
    }

    /// <summary>
    /// Marks whether the step should participate in the current plan build.
    /// </summary>
    public ExecutionStepBuilder When(bool enabled, string? disabledReason = null)
    {
        _owner.SetCondition(_definition, enabled, disabledReason);
        return this;
    }

    /// <summary>
    /// Overrides the step description without forcing the title declaration to carry long explanatory text.
    /// </summary>
    public ExecutionStepBuilder Describe(string? description)
    {
        _owner.SetDescription(_definition, description);
        return this;
    }

    /// <summary>
    /// Attaches an async callback to the step and returns its typed handle.
    /// </summary>
    public ExecutionStepHandle Then(Func<ExecutionTaskContext, Task> executeAsync)
    {
        if (executeAsync == null)
        {
            throw new ArgumentNullException(nameof(executeAsync));
        }

        _owner.AttachCallback(_definition, async context =>
        {
            await executeAsync(context);
            return OperationResult.Succeeded();
        });
        return Handle;
    }

    /// <summary>
    /// Attaches an async callback that returns an explicit operation result and returns the typed step handle.
    /// </summary>
    public ExecutionStepHandle Then(Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        _owner.AttachCallback(_definition, executeAsync);
        return Handle;
    }

    /// <summary>
    /// Attaches a parameterless async callback and returns the typed step handle.
    /// </summary>
    public ExecutionStepHandle Then(Func<Task> executeAsync)
    {
        if (executeAsync == null)
        {
            throw new ArgumentNullException(nameof(executeAsync));
        }

        _owner.AttachCallback(_definition, async _ =>
        {
            await executeAsync();
            return OperationResult.Succeeded();
        });
        return Handle;
    }
}
