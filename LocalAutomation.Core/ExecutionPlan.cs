using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace LocalAutomation.Core;

/// <summary>
/// Represents the previewable execution DAG for an operation, including tasks, dependencies, and lightweight
/// validation helpers for UI and runtime consumers.
/// </summary>
public sealed class ExecutionPlan
{
    /// <summary>
    /// Creates an execution plan from the provided metadata, tasks, and dependencies.
    /// </summary>
    public ExecutionPlan(ExecutionPlanId id, string title, IEnumerable<ExecutionTask> tasks, IEnumerable<ExecutionDependency>? dependencies = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Execution plan title is required.", nameof(title));
        }

        Id = id;
        Title = title;
        List<ExecutionTask> materializedTasks = (tasks ?? throw new ArgumentNullException(nameof(tasks))).ToList();
        List<ExecutionDependency> materializedDependencies = (dependencies ?? Array.Empty<ExecutionDependency>()).ToList();
        Validate(materializedTasks, materializedDependencies);
        Tasks = new ReadOnlyCollection<ExecutionTask>(materializedTasks);
        Dependencies = new ReadOnlyCollection<ExecutionDependency>(materializedDependencies);
    }

    /// <summary>
    /// Gets the stable identifier for the plan.
    /// </summary>
    public ExecutionPlanId Id { get; }

    /// <summary>
    /// Gets the display title for the plan.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the tasks contained in the plan.
    /// </summary>
    public IReadOnlyList<ExecutionTask> Tasks { get; }

    /// <summary>
    /// Gets the dependencies contained in the plan.
    /// </summary>
    public IReadOnlyList<ExecutionDependency> Dependencies { get; }

    /// <summary>
    /// Returns the task with the provided identifier when it exists.
    /// </summary>
    public ExecutionTask? GetTask(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return Tasks.FirstOrDefault(task => task.Id == taskId.Value);
    }

    /// <summary>
    /// Returns the task identifiers that must complete before the provided task can run.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> GetTaskDependencies(ExecutionTaskId taskId)
    {
        return Dependencies
            .Where(dependency => dependency.TargetTaskId == taskId)
            .Select(dependency => dependency.SourceTaskId)
            .ToList();
    }

    /// <summary>
    /// Returns the task identifiers that directly depend on the provided task.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> GetDependentTasks(ExecutionTaskId taskId)
    {
        return Dependencies
            .Where(dependency => dependency.SourceTaskId == taskId)
            .Select(dependency => dependency.TargetTaskId)
            .ToList();
    }

    /// <summary>
    /// Validates the supplied task and dependency set so hosts can trust that the plan is a well-formed DAG.
    /// </summary>
    private static void Validate(IReadOnlyList<ExecutionTask> tasks, IReadOnlyList<ExecutionDependency> dependencies)
    {
        if (tasks.Count == 0)
        {
            throw new InvalidOperationException("Execution plans must contain at least one task.");
        }

        Dictionary<ExecutionTaskId, ExecutionTask> tasksById = new();
        foreach (ExecutionTask task in tasks)
        {
            if (!tasksById.TryAdd(task.Id, task))
            {
                throw new InvalidOperationException($"Execution plan contains duplicate task id '{task.Id}'.");
            }
        }

        foreach (ExecutionTask task in tasks)
        {
            if (task.ParentId is ExecutionTaskId parentId && !tasksById.ContainsKey(parentId))
            {
                throw new InvalidOperationException($"Execution task '{task.Id}' references missing parent '{task.ParentId}'.");
            }
        }

        foreach (ExecutionDependency dependency in dependencies)
        {
            if (!tasksById.ContainsKey(dependency.SourceTaskId))
            {
                throw new InvalidOperationException($"Execution dependency references missing source task '{dependency.SourceTaskId}'.");
            }

            if (!tasksById.ContainsKey(dependency.TargetTaskId))
            {
                throw new InvalidOperationException($"Execution dependency references missing target task '{dependency.TargetTaskId}'.");
            }
        }
    }
}
