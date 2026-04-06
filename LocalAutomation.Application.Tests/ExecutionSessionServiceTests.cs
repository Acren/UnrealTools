using System;
using System.Threading.Tasks;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using TestUtilities;
using Xunit;

namespace LocalAutomation.Application.Tests;

public sealed class ExecutionSessionServiceTests
{
    /// <summary>
    /// Confirms that a background execution-session exception is forwarded to the process-wide application logger with
    /// the original exception attached so the App Log can surface the runtime failure.
    /// </summary>
    [Fact]
    public async Task BackgroundExecutionExceptionIsLoggedToApplicationLogger()
    {
        /* Install a capturing application logger because ExecutionSessionService runs the session on a fire-and-forget
           task and the app log is the durable surface that must receive the exception. */
        ApplicationTestUtilities.CapturingLogger logger = new();
        ApplicationLogger.Logger = logger;

        ExecutionSessionService service = new();
        InvalidOperationException syntheticException = new("Synthetic background failure.");

        /* Inject the synthetic exception through requirements evaluation so the failure escapes ExecutionSession.Run()
           before any scheduler task-level logging can handle it. That isolates the session-service app-log path without
           needing a one-off operation type. */
        Operation operation = new ExecutionTestCommon.InlineOperation(
            operationName: "Application Test Operation",
            checkRequirements: _ => throw syntheticException);
        OperationParameters parameters = operation.CreateParameters();
        parameters.Target = new ExecutionTestCommon.TestTarget();

        /* Start the background execution and wait specifically for the forwarded application-log error rather than for
           session completion, because the session-service catch logs asynchronously after the background run faults. */
        _ = service.StartExecution(operation, parameters);
        ApplicationTestUtilities.CapturingLogger.CapturedLogEntry entry = await logger.WaitForErrorAsync(
            captured => captured.Message.Contains("Execution session", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        /* The application logger must receive the original synthetic exception so the App Log preserves the real failure
           object and message instead of silently swallowing the background fault. */
        InvalidOperationException exception = Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Execution session", entry.Message, StringComparison.Ordinal);
        Assert.Same(syntheticException, exception);
        Assert.Contains("Synthetic background failure.", exception.Message, StringComparison.Ordinal);
    }
}
