using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LocalAutomation.Runtime;

/// <summary>
/// Shares presentation-oriented builder configuration for authored tasks and imported child-operation roots so graph-
/// facing settings stay consistent without duplicating implementation.
/// </summary>
public abstract class ExecutionNodeBuilderBase<TBuilder>
    where TBuilder : ExecutionNodeBuilderBase<TBuilder>
{
    /// <summary>
    /// Overrides the node description without forcing long text into the creation call.
    /// </summary>
    public TBuilder Describe(string? description)
    {
        SetDescription(description);
        return (TBuilder)this;
    }

    /// <summary>
    /// Marks whether this node should be collapsed out of the graph projection until hidden tasks are revealed.
    /// </summary>
    public TBuilder HideInGraph(bool hidden = true)
    {
        SetHiddenInGraph(hidden);
        return (TBuilder)this;
    }

    protected abstract void SetDescription(string? description);

    protected abstract void SetHiddenInGraph(bool hidden);
}

/// <summary>
/// Provides the fluent authoring surface for one declared task inside an execution plan.
/// </summary>
public sealed class ExecutionTaskBuilder : ExecutionNodeBuilderBase<ExecutionTaskBuilder>
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionTask _task;
    private readonly ExecutionTaskId? _parentId;
    private readonly IList<ExecutionTaskId>? _lastTaskIds;

    internal ExecutionTaskBuilder(ExecutionPlanBuilder owner, ExecutionTask task, ExecutionTaskId? parentId, IList<ExecutionTaskId>? lastTaskIds = null)
    {
        _owner = owner;
        _task = task;
        _parentId = parentId;
        _lastTaskIds = lastTaskIds;
        _owner.SetOperationParameters(_task, owner.OperationParameters);
        Id = _task.Id;
    }

    /// <summary>
    /// Gets the task id for the declared task.
    /// </summary>
    public ExecutionTaskId Id { get; }

    protected override void SetDescription(string? description)
    {
        _owner.SetDescription(_task, description);
    }

    protected override void SetHiddenInGraph(bool hidden)
    {
        _owner.SetGraphVisibility(_task, hidden);
    }

    /// <summary>
    /// Declares that this task depends on the provided earlier task.
    /// </summary>
    public ExecutionTaskBuilder After(ExecutionTaskId dependencyId)
    {
        _owner.AddTaskDependency(_task, dependencyId);
        return this;
    }

    /// <summary>
    /// Declares that this task depends on each provided earlier task.
    /// </summary>
    public ExecutionTaskBuilder After(params ExecutionTaskId[] dependencyIds)
    {
        foreach (ExecutionTaskId dependencyId in dependencyIds)
        {
            _owner.AddTaskDependency(_task, dependencyId);
        }

        return this;
    }

    /// <summary>
    /// Marks whether the task should participate in the current plan build.
    /// </summary>
    public ExecutionTaskBuilder When(bool enabled, string? disabledReason = null)
    {
        _owner.SetCondition(_task, enabled, disabledReason);
        return this;
    }

    /// <summary>
    /// Attaches the async execution body for this task.
    /// </summary>
    public ExecutionTaskBuilder Run(Func<ExecutionTaskContext, Task> executeAsync)
    {
        return Run(executeAsync, out _);
    }

    /// <summary>
    /// Attaches the async execution body for this task and exposes the generated executable body task id.
    /// </summary>
    public ExecutionTaskBuilder Run(Func<ExecutionTaskContext, Task> executeAsync, out ExecutionTaskId executionTaskId)
    {
        if (executeAsync == null)
        {
            throw new ArgumentNullException(nameof(executeAsync));
        }

        executionTaskId = _owner.AttachBodyTask(_task, async context =>
        {
            await executeAsync(context);
            return OperationResult.Succeeded();
        });
        return this;
    }

    /// <summary>
    /// Attaches the async execution body for this task when the body needs to return an explicit operation result.
    /// </summary>
    public ExecutionTaskBuilder Run(Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        return Run(executeAsync, out _);
    }

    /// <summary>
    /// Attaches the async execution body for this task when the body needs to return an explicit operation result and
    /// exposes the generated executable body task id.
    /// </summary>
    public ExecutionTaskBuilder Run(Func<ExecutionTaskContext, Task<OperationResult>> executeAsync, out ExecutionTaskId executionTaskId)
    {
        executionTaskId = _owner.AttachBodyTask(_task, executeAsync);
        return this;
    }

    /// <summary>
    /// Attaches the async execution body for this task when no execution context is needed.
    /// </summary>
    public ExecutionTaskBuilder Run(Func<Task> executeAsync)
    {
        return Run(executeAsync, out _);
    }

    /// <summary>
    /// Attaches the async execution body for this task when no execution context is needed and exposes the generated
    /// executable body task id.
    /// </summary>
    public ExecutionTaskBuilder Run(Func<Task> executeAsync, out ExecutionTaskId executionTaskId)
    {
        if (executeAsync == null)
        {
            throw new ArgumentNullException(nameof(executeAsync));
        }

        executionTaskId = _owner.AttachBodyTask(_task, async _ =>
        {
            await executeAsync();
            return OperationResult.Succeeded();
        });
        return this;
    }

    /// <summary>
    /// Declares one direct child task beneath the current task and immediately advances the parent's authored child
    /// frontier to the new child task.
    /// </summary>
    public ExecutionTaskBuilder Child(string title, string? description = null)
    {
        return _owner.DeclareSequentialRelativeTask(Id, title, description);
    }

    /// <summary>
    /// Declares one child operation using the child operation's own default root title.
    /// </summary>
    public ExecutionChildOperationBuilder AddChildOperation<TOperation>(Func<OperationParameters> createParameters)
        where TOperation : Operation, new()
    {
        Operation childOperation = Operation.CreateOperation(typeof(TOperation));
        return AddChildOperation(childOperation, createParameters);
    }

    /// <summary>
    /// Declares one child operation beneath the current task and returns a builder that configures the imported child root
    /// directly after the child plan is expanded and inserted.
    /// </summary>
    public ExecutionChildOperationBuilder AddChildOperation<TOperation>(string title, Func<OperationParameters> createParameters, string? description = null)
        where TOperation : Operation, new()
    {
        Operation childOperation = Operation.CreateOperation(typeof(TOperation));
        return AddChildOperation(title, childOperation, createParameters, description);
    }

    /// <summary>
    /// Declares one specific child operation instance using that instance's own default root title. This allows callers
    /// to author inline or stateful child operations without creating a one-off operation type only to satisfy the builder
    /// API.
    /// </summary>
    public ExecutionChildOperationBuilder AddChildOperation(Operation childOperation, Func<OperationParameters> createParameters)
    {
        _ = childOperation ?? throw new ArgumentNullException(nameof(childOperation));
        return AddChildOperation(childOperation.OperationName, childOperation, createParameters);
    }

    /// <summary>
    /// Declares one specific child operation instance beneath the current task and returns a builder that configures the
    /// imported child root directly after the child plan is expanded and inserted.
    /// </summary>
    public ExecutionChildOperationBuilder AddChildOperation(string title, Operation childOperation, Func<OperationParameters> createParameters, string? description = null)
    {
        return _owner.AttachChildOperation(_task, childOperation, createParameters, title, description);
    }

    /// <summary>
    /// Declares one sequential sibling task under the same parent by depending on this visible authored task directly.
    /// </summary>
    public ExecutionTaskBuilder Then(string title, string? description = null)
    {
        ExecutionTaskId parentId = _parentId ?? throw new InvalidOperationException("Root tasks cannot declare sequential siblings.");
        return _owner.DeclareNextSiblingTask(_task.Id, parentId, title, description, _lastTaskIds);
    }

    /// <summary>
    /// Opens a child-task scope beneath this task so nested task hierarchies remain explicit in the builder.
    /// </summary>
    public ExecutionTaskBuilder Children(Action<ExecutionTaskScopeBuilder> build)
    {
        return Children(ExecutionChildMode.Sequenced, build);
    }

    /// <summary>
    /// Opens a child-task scope beneath this task and lets the scope own the transient sibling frontier needed to model
    /// sequential or parallel child declarations.
    /// </summary>
    public ExecutionTaskBuilder Children(ExecutionChildMode mode, Action<ExecutionTaskScopeBuilder> build)
    {
        if (build == null)
        {
            throw new ArgumentNullException(nameof(build));
        }

        _owner.BuildChildScope(_task, mode, build);
        return this;
    }
}

/// <summary>
/// Configures one imported child-operation root directly after the child plan has been inserted beneath its parent task.
/// </summary>
public sealed class ExecutionChildOperationBuilder : ExecutionNodeBuilderBase<ExecutionChildOperationBuilder>
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionTask _task;

    internal ExecutionChildOperationBuilder(ExecutionPlanBuilder owner, ExecutionTask task)
    {
        _owner = owner;
        _task = task;
    }

    protected override void SetDescription(string? description)
    {
        _owner.SetDescription(_task, description);
    }

    protected override void SetHiddenInGraph(bool hidden)
    {
        _owner.SetGraphVisibility(_task, hidden);
    }
}
