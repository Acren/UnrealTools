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
    /// Confirms that a background execution-session exception is forwarded to the session logger so the execution tab and
    /// per-session log file can surface the runtime failure.
    /// </summary>
    [Fact]
    public async Task BackgroundExecutionExceptionIsLoggedToSessionLogger()
    {
        ExecutionSessionService service = new();
        InvalidOperationException syntheticException = new("Synthetic background failure.");
        TaskCompletionSource<LogEntry> sessionErrorSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /* Inject the synthetic exception through requirements evaluation so the failure escapes ExecutionSession.Run()
           before any scheduler task-level logging can handle it. That isolates the session-service session-log path
           without needing a one-off operation type. */
        Operation operation = new ExecutionTestCommon.InlineOperation(
            operationName: "Application Test Operation",
            checkRequirements: _ => throw syntheticException);
        OperationParameters parameters = operation.CreateParameters();
        parameters.Target = new ExecutionTestCommon.TestTarget();

        void CaptureSessionError(LogEntry entry)
        {
            if (entry.Verbosity >= LogLevel.Error && entry.Message.Contains("Execution session", StringComparison.Ordinal))
            {
                sessionErrorSource.TrySetResult(entry);
            }
        }

        /* Attach the session-log observer before execution starts so the fire-and-forget background path cannot emit the
           session-service error before the test is listening. */
        LocalAutomation.Runtime.ExecutionSession session = service.StartExecution(operation, parameters, createdSession =>
        {
            createdSession.LogStream.EntryAdded += CaptureSessionError;
        });

        try
        {
            LogEntry entry = await sessionErrorSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

            /* The session logger stringifies the original exception into the session stream, preserving the actionable
               failure details without routing routine execution output through the process-wide application logger. */
            Assert.Equal(LogLevel.Error, entry.Verbosity);
            Assert.Equal(session.Id.Value, entry.SessionId);
            Assert.Null(entry.TaskId);
            Assert.Contains("Execution session", entry.Message, StringComparison.Ordinal);
            Assert.Contains("Synthetic background failure.", entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            session.LogStream.EntryAdded -= CaptureSessionError;
        }
    }
}
