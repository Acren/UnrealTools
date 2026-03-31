using System;
using System.Threading.Tasks;

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides the fluent authoring surface for one declared task inside an execution plan.
/// </summary>
public sealed class ExecutionTaskBuilder
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionPlanBuilder.PlanItemDefinition _definition;
    private readonly ExecutionTaskHandle _parent;

    internal ExecutionTaskBuilder(ExecutionPlanBuilder owner, ExecutionPlanBuilder.PlanItemDefinition definition, ExecutionTaskHandle parent)
    {
        _owner = owner;
        _definition = definition;
        _parent = parent;
        Handle = _owner.FinalizeTask(_definition);
    }

    /// <summary>
    /// Gets the typed handle for the declared task.
    /// </summary>
    public ExecutionTaskHandle Handle { get; }

    /// <summary>
    /// Declares that this task depends on the provided earlier task.
    /// </summary>
    public ExecutionTaskBuilder After(ExecutionTaskHandle dependency)
    {
        _owner.AddDependency(_definition, dependency);
        return this;
    }

    /// <summary>
    /// Declares that this task depends on each provided earlier task.
    /// </summary>
    public ExecutionTaskBuilder After(params ExecutionTaskHandle[] dependencies)
    {
        foreach (ExecutionTaskHandle dependency in dependencies)
        {
            _owner.AddDependency(_definition, dependency);
        }

        return this;
    }

    /// <summary>
    /// Marks whether the task should participate in the current plan build.
    /// </summary>
    public ExecutionTaskBuilder When(bool enabled, string? disabledReason = null)
    {
        _owner.SetCondition(_definition, enabled, disabledReason);
        return this;
    }

    /// <summary>
    /// Overrides the task description without forcing long text into the Task(...) call.
    /// </summary>
    public ExecutionTaskBuilder Describe(string? description)
    {
        _owner.SetDescription(_definition, description);
        return this;
    }

    /// <summary>
    /// Attaches an async callback to the task.
    /// </summary>
    public ExecutionTaskBuilder Then(Func<ExecutionTaskContext, Task> executeAsync)
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
        return this;
    }

    /// <summary>
    /// Attaches an async callback that returns an explicit operation result.
    /// </summary>
    public ExecutionTaskBuilder Then(Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        _owner.AttachCallback(_definition, executeAsync);
        return this;
    }

    /// <summary>
    /// Declares that this task's subtree is provided by another operation whose plan the framework should expand.
    /// </summary>
    public ExecutionTaskBuilder ExpandChildOperation<TOperation>(Func<OperationParameters> createParameters)
        where TOperation : Operation, new()
    {
        _owner.AttachChildOperation(_definition, typeof(TOperation), createParameters);
        return this;
    }

    /// <summary>
    /// Attaches a parameterless async callback to the task.
    /// </summary>
    public ExecutionTaskBuilder Then(Func<Task> executeAsync)
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
        return this;
    }

    /// <summary>
     /// Declares one sequential sibling task under the same parent as this task and automatically depends on this task.
     /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null)
    {
        ExecutionTaskBuilder nextTask = _owner.Task(title, description, _parent);
        nextTask.After(Handle);
        return nextTask;
    }

    /// <summary>
    /// Opens a child-task scope beneath this task so nested task hierarchies remain explicit in the builder.
    /// </summary>
    public ExecutionTaskBuilder Children(Action<ExecutionTaskScopeBuilder> build)
    {
        if (build == null)
        {
            throw new ArgumentNullException(nameof(build));
        }

        build(new ExecutionTaskScopeBuilder(_owner, Handle));
        return this;
    }

    /// <summary>
    /// Imports the tasks from one authored sub-plan beneath this task.
    /// </summary>
    public ExecutionTaskBuilder Import(ExecutionPlan plan)
    {
        _owner.ImportPlan(plan, Handle);
        return this;
    }
}
