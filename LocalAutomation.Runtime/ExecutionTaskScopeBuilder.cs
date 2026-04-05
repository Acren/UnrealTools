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
            if (!_hasDeclaredTasks)
            {
                _completionFrontier.Clear();
            }

            _hasDeclaredTasks = true;
            return _owner.DeclareScopedParallelRelativeTask(_parentId, title, description, _incomingFrontier, _completionFrontier);
        }

        _hasDeclaredTasks = true;
        return _owner.DeclareScopedSequentialRelativeTask(_parentId, title, description, _completionFrontier);
    }
}
