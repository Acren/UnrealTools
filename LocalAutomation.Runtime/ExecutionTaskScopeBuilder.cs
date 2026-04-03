using System;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds one sibling scope of tasks beneath a shared parent and optionally auto-sequences repeated Task(...) calls.
/// </summary>
public sealed class ExecutionTaskScopeBuilder
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionTaskHandle _parent;
    private readonly ExecutionChildMode _mode;
    private ExecutionTaskBuilder? _lastTask;
    private ExecutionPlanBuilder.ChildDeclarationEntry? _parallelScopeEntry;

    internal ExecutionTaskScopeBuilder(ExecutionPlanBuilder owner, ExecutionTaskHandle parent, ExecutionChildMode mode)
    {
        _owner = owner;
        _parent = parent;
        _mode = mode;
    }

    /// <summary>
     /// Declares the next sibling task in this scope.
     /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null)
    {
        ExecutionTaskBuilder task = _owner.Task(title, description, _parent);
        if (_mode == ExecutionChildMode.Parallel)
        {
            _parallelScopeEntry ??= _owner.RegisterChildScopeEntry(_parent);
            _owner.AddScopeEntryTask(_parallelScopeEntry, task.Definition);
            _owner.AddScopeCompletionTask(_parallelScopeEntry, task.Definition);
        }
        else
        {
            _owner.RegisterChildScopeTaskEntry(_parent, task.Definition);
        }

        _lastTask = task;
        return task;
    }

}
