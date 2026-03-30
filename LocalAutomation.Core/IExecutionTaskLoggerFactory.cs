using Microsoft.Extensions.Logging;

namespace LocalAutomation.Core;

/// <summary>
/// Creates loggers that route output into a specific execution-plan task while preserving the wider session and
/// application log fan-out.
/// </summary>
public interface IExecutionTaskLoggerFactory
{
    /// <summary>
    /// Creates a logger that attributes all emitted output to the provided execution task identifier.
    /// </summary>
    ILogger CreateTaskLogger(ExecutionTaskId taskId);
}
