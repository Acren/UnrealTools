using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents the authored execution plan for an operation, including the plan title and the tasks that make up the
/// operation's work.
/// </summary>
public sealed class ExecutionPlan
{
    /// <summary>
    /// Creates an execution plan from the provided metadata and tasks.
    /// </summary>
    public ExecutionPlan(ExecutionPlanId id, string title, IEnumerable<ExecutionTask> tasks)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Execution plan title is required.", nameof(title));
        }

        Id = id;
        Title = title;
        List<ExecutionTask> materializedTasks = (tasks ?? throw new ArgumentNullException(nameof(tasks))).ToList();
        Validate(materializedTasks);
        Tasks = new ReadOnlyCollection<ExecutionTask>(materializedTasks);
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
    /// Returns the task identifiers that must complete before the provided task can run. Dependencies are stored
    /// directly on each task as ID values.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> GetTaskDependencies(ExecutionTaskId taskId)
    {
        return Tasks.Single(task => task.Id == taskId).Dependencies;
    }

    /// <summary>
    /// Validates the supplied task set so hosts can trust that the plan is a well-formed DAG. Dependency integrity
    /// is guaranteed structurally because dependencies are authored as task identifiers that must resolve within the
    /// same plan.
    /// </summary>
    private static void Validate(IReadOnlyList<ExecutionTask> tasks)
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

        int rootTaskCount = tasks.Count(task => task.ParentId == null);
        if (rootTaskCount != 1)
        {
            throw new InvalidOperationException(rootTaskCount == 0
                ? "Execution plans must contain exactly one root task, but none were found."
                : $"Execution plans must contain exactly one root task, but found {rootTaskCount}.");
        }
    }
}
