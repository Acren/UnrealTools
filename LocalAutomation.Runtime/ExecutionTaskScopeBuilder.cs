using System;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds one sibling scope of tasks beneath a shared parent and optionally auto-sequences repeated Task(...) calls.
/// </summary>
public sealed class ExecutionTaskScopeBuilder
{
    private readonly ExecutionPlanBuilder _owner;
    private readonly ExecutionTaskId _parentId;
    private readonly ExecutionChildMode _mode;
    private ExecutionPlanBuilder.ChildDeclarationEntry? _parallelScopeEntry;

    internal ExecutionTaskScopeBuilder(ExecutionPlanBuilder owner, ExecutionTaskId parentId, ExecutionChildMode mode)
    {
        _owner = owner;
        _parentId = parentId;
        _mode = mode;
    }

    /// <summary>
    /// Declares the next task in this child scope, reusing one shared declaration entry when the scope is parallel.
    /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null)
    {
        return _mode == ExecutionChildMode.Parallel
            ? _owner.DeclareParallelRelativeTask(_parentId, title, description, ref _parallelScopeEntry)
            : _owner.DeclareSequentialRelativeTask(_parentId, title, description);
    }

}
