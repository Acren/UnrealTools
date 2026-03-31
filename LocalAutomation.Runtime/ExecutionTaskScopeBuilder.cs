using System;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds one sibling scope of tasks beneath a shared parent and automatically sequences repeated Task(...) calls.
/// </summary>
public sealed class ExecutionTaskScopeBuilder
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionTaskHandle _parent;
    private ExecutionTaskBuilder? _lastTask;

    internal ExecutionTaskScopeBuilder(ExecutionPlanBuilder owner, ExecutionTaskHandle parent)
    {
        _owner = owner;
        _parent = parent;
    }

    /// <summary>
    /// Declares the next sibling task in this scope.
    /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null)
    {
        ExecutionTaskBuilder task = _owner.Task(title, description, _parent);
        if (_lastTask != null)
        {
            task.After(_lastTask.Handle);
        }

        _lastTask = task;
        return task;
    }

    /// <summary>
    /// Declares the next sibling task in this scope with an explicit stable identifier.
    /// </summary>
    public ExecutionTaskBuilder Task(ExecutionTaskId id, string title, string? description = null)
    {
        ExecutionTaskBuilder task = _owner.Task(id, title, description, _parent);
        if (_lastTask != null)
        {
            task.After(_lastTask.Handle);
        }

        _lastTask = task;
        return task;
    }
}
