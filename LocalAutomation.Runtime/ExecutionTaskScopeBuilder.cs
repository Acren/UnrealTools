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
    private ExecutionPlanBuilder.ChildDeclarationEntry? _parallelScopeEntry;

    internal ExecutionTaskScopeBuilder(ExecutionPlanBuilder owner, ExecutionTaskHandle parent, ExecutionChildMode mode)
    {
        _owner = owner;
        _parent = parent;
        _mode = mode;
    }

    /// <summary>
    /// Declares the next task in this child scope, reusing one shared declaration entry when the scope is parallel.
    /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null)
    {
        return _mode == ExecutionChildMode.Parallel
            ? _owner.DeclareRelativeTask(_parent, title, description, TaskPlacement.ChildParallel, ref _parallelScopeEntry)
            : _owner.DeclareRelativeTask(_parent, title, description, TaskPlacement.ChildSequential);
    }

}
