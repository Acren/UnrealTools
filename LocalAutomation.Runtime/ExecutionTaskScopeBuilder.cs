using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds one sibling scope of tasks beneath a shared parent and owns the transient completion frontier needed to model
/// sequenced or parallel repeated Task(...) declarations.
/// </summary>
public sealed class ExecutionTaskScopeBuilder
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionTaskId _parentId;
    private readonly ExecutionChildMode _mode;
    private readonly IReadOnlyList<ExecutionTaskId> _incomingFrontier;
    private readonly List<ExecutionTaskId> _completionFrontier;
    private bool _hasDeclaredTasks;

    internal ExecutionTaskScopeBuilder(ExecutionPlanBuilder owner, ExecutionTaskId parentId, ExecutionChildMode mode, IReadOnlyList<ExecutionTaskId> startingFrontier)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _parentId = parentId;
        _mode = mode;
        _incomingFrontier = startingFrontier?.ToList() ?? throw new ArgumentNullException(nameof(startingFrontier));
        _completionFrontier = startingFrontier.ToList();
    }

    internal IReadOnlyList<ExecutionTaskId> CompletionFrontier => _completionFrontier;

    /// <summary>
    /// Declares the next task in this child scope, either appending to the current sequential frontier or joining the
    /// parallel scope's shared completion frontier.
    /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null)
    {
        if (_mode == ExecutionChildMode.Parallel)
        {
            PrepareParallelDeclaration();
            return _owner.DeclareScopedParallelRelativeTask(_parentId, title, description, _incomingFrontier, _completionFrontier);
        }

        _hasDeclaredTasks = true;
        return _owner.DeclareScopedSequentialRelativeTask(_parentId, title, description, _completionFrontier);
    }

    /// <summary>
    /// Declares the next task in this child scope and copies out the authored task id so callers can capture stable
    /// task identity directly from Task(...) without depending on the returned builder afterwards.
    /// </summary>
    public ExecutionTaskBuilder Task(string title, out ExecutionTaskId taskId, string? description = null)
    {
        ExecutionTaskBuilder builder = Task(title, description);
        taskId = builder.Id;
        return builder;
    }

    /// <summary>
    /// Declares one child operation in this sibling scope using the operation's own default root title.
    /// </summary>
    public ExecutionTaskBuilder AddChildOperation<TOperation>(
        Func<OperationParameters> createParameters,
        Func<IOperationParameterContext, OperationParameters>? createRuntimeParameters = null)
        where TOperation : Operation, new()
    {
        Operation childOperation = Operation.CreateOperation(typeof(TOperation));
        return AddChildOperation(childOperation, createParameters, createRuntimeParameters);
    }

    /// <summary>
    /// Declares one child operation in this sibling scope using a parent-side title and description override.
    /// </summary>
    public ExecutionTaskBuilder AddChildOperation<TOperation>(
        string title,
        Func<OperationParameters> createParameters,
        string? description = null,
        Func<IOperationParameterContext, OperationParameters>? createRuntimeParameters = null)
        where TOperation : Operation, new()
    {
        Operation childOperation = Operation.CreateOperation(typeof(TOperation));
        return AddChildOperation(title, childOperation, createParameters, description, createRuntimeParameters);
    }

    /// <summary>
    /// Declares one specific child operation instance in this sibling scope using that operation's default root title.
    /// </summary>
    public ExecutionTaskBuilder AddChildOperation(
        Operation childOperation,
        Func<OperationParameters> createParameters,
        Func<IOperationParameterContext, OperationParameters>? createRuntimeParameters = null)
    {
        _ = childOperation ?? throw new ArgumentNullException(nameof(childOperation));
        return AddChildOperation(childOperation.OperationName, childOperation, createParameters, createRuntimeParameters: createRuntimeParameters);
    }

    /// <summary>
    /// Declares one specific child operation instance as a direct sibling of normal tasks in this scope.
    /// </summary>
    public ExecutionTaskBuilder AddChildOperation(
        string title,
        Operation childOperation,
        Func<OperationParameters> createParameters,
        string? description = null,
        Func<IOperationParameterContext, OperationParameters>? createRuntimeParameters = null)
    {
        _ = childOperation ?? throw new ArgumentNullException(nameof(childOperation));
        if (_mode == ExecutionChildMode.Parallel)
        {
            PrepareParallelDeclaration();
            return _owner.DeclareScopedParallelChildOperation(_parentId, childOperation, createParameters, title, description, createRuntimeParameters, _incomingFrontier, _completionFrontier);
        }

        _hasDeclaredTasks = true;
        return _owner.DeclareScopedSequentialChildOperation(_parentId, childOperation, createParameters, title, description, createRuntimeParameters, _completionFrontier);
    }

    /// <summary>
    /// Switches a parallel scope from inherited incoming frontier to the union of explicitly declared sibling leaves.
    /// </summary>
    private void PrepareParallelDeclaration()
    {
        if (!_hasDeclaredTasks)
        {
            _completionFrontier.Clear();
        }

        _hasDeclaredTasks = true;
    }
}
