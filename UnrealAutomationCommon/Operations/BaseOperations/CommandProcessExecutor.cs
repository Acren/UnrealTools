using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#nullable enable

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    /// <summary>
    /// Executes one command-backed task with the same logging, cancellation, and exit-code handling used by operations.
    /// </summary>
    internal static class CommandProcessExecutor
    {
        /// <summary>
        /// Runs the supplied command as the body of the current execution task and returns the process result.
        /// </summary>
        internal static async Task<global::LocalAutomation.Runtime.OperationResult> ExecuteAsync(
            global::LocalAutomation.Runtime.ExecutionTaskContext context,
            global::LocalAutomation.Runtime.Command command,
            string operationTypeName,
            Action<string>? onOutputLine = null)
        {
            using global::LocalAutomation.Core.PerformanceActivityScope activity = global::LocalAutomation.Core.PerformanceTelemetry.StartActivity("CommandProcessOperation.ExecuteProcess")
                .SetTag("task.id", context.TaskId.Value)
                .SetTag("task.title", context.Title)
                .SetTag("operation.type", operationTypeName);

            ILogger logger = context.Logger;
            CommandProcessState state = new(Path.GetFileName(command.File));

            logger.LogInformation("Running command: " + command);
            if (!File.Exists(command.File))
            {
                throw new FileNotFoundException("Process command file was not found.", command.File);
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = command.File,
                Arguments = command.Arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (global::LocalAutomation.Core.PerformanceActivityScope startProcessActivity = global::LocalAutomation.Core.PerformanceTelemetry.StartActivity("CommandProcessOperation.StartProcess"))
            {
                state.Process = new Process { StartInfo = startInfo };
                state.Process.EnableRaisingEvents = true;
                state.Process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data != null)
                    {
                        HandleLogLine(logger, args.Data, onOutputLine);
                    }
                };
                state.Process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data != null)
                    {
                        logger.LogError(args.Data);
                    }
                };
                state.Process.Start();
                state.Process.BeginOutputReadLine();
                state.Process.BeginErrorReadLine();
                startProcessActivity.SetTag("process.id", state.Process.Id);
            }

            state.ProcessName = state.Process.ProcessName;
            activity.SetTag("process.name", state.ProcessName)
                .SetTag("process.file", state.FileName ?? string.Empty);

            logger.LogInformation("Launched process '" + state.FileAndProcess + "'");
            TaskCompletionSource<int> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Process.Exited += (_, _) =>
            {
                logger.LogDebug($"Process '{state.FileAndProcess}' exited");
                tcs.TrySetResult(0);
            };
            if (state.Process.HasExited)
            {
                // Very short-lived commands can exit before the handler is attached even with raising enabled.
                tcs.TrySetResult(0);
            }

            /* Register the live process-cancellation callback only after launch so terminate requests can report the
               concrete process identity they attempted to stop. */
            CancellationTokenRegistration registration = context.CancellationToken.Register(() => TryTerminateProcessForCancellation(logger, state, operationTypeName));

            await tcs.Task.ConfigureAwait(false);

            // Dispose the registration so the cancellation callback does not keep this task state alive after exit.
            await registration.DisposeAsync().ConfigureAwait(false);

            return HandleProcessEnded(logger, state.Process, state.FileAndProcess, state.WasCancelled);
        }

        /// <summary>
        /// Classifies one process output line and lets callers inspect stdout without storing the full process log.
        /// </summary>
        private static void HandleLogLine(ILogger logger, string line, Action<string>? onOutputLine)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            // The streaming callback runs before log classification so operations can parse raw tool output.
            onOutputLine?.Invoke(line);

            string[] split = line.Split(new[] { ": " }, StringSplitOptions.None);
            LogLevel level = LogLevel.Information;
            if (split.Length > 1)
            {
                if (split[0] == "ERROR")
                {
                    // UBT emits process-level errors as "ERROR: message" lines.
                    level = LogLevel.Error;
                }
                else if (split[1] == "Error")
                {
                    // Unreal logs category errors as "LogCategory: Error: message" lines.
                    level = LogLevel.Error;
                }
                else if (split[1] == "Warning")
                {
                    // Unreal logs category warnings as "LogCategory: Warning: message" lines.
                    level = LogLevel.Warning;
                }
            }

            if (line.Contains("): error") || line.Contains(" : error ") || line.Contains("): fatal error") || line.Contains(" : fatal error"))
            {
                // Compiler diagnostics use file/line prefixes rather than Unreal log categories.
                level = LogLevel.Error;
            }
            else if (line.Contains("): warning") || line.Contains(" : warning "))
            {
                // Compiler warnings should contribute to task warning counts like Unreal warning lines.
                level = LogLevel.Warning;
            }

            logger.Log(level, line);
        }

        /// <summary>
        /// Converts the completed process exit state into the runtime operation result and logs the final status.
        /// </summary>
        private static global::LocalAutomation.Runtime.OperationResult HandleProcessEnded(ILogger logger, Process process, string fileAndProcess, bool wasCancelled)
        {
            global::LocalAutomation.Runtime.OperationResult result = wasCancelled
                ? global::LocalAutomation.Runtime.OperationResult.Cancelled(process.ExitCode)
                : process.ExitCode == 0
                    ? global::LocalAutomation.Runtime.OperationResult.Succeeded(process.ExitCode)
                    : global::LocalAutomation.Runtime.OperationResult.Failed(process.ExitCode);

            LogLevel exitLevel = result.Outcome == global::LocalAutomation.Runtime.ExecutionTaskOutcome.Cancelled
                ? LogLevel.Warning
                : result.Outcome == global::LocalAutomation.Runtime.ExecutionTaskOutcome.Completed
                    ? LogLevel.Information
                    : LogLevel.Error;
            string exitLabel = result.Outcome == global::LocalAutomation.Runtime.ExecutionTaskOutcome.Cancelled
                ? "cancelled"
                : result.Outcome == global::LocalAutomation.Runtime.ExecutionTaskOutcome.Completed
                    ? "succeeded"
                    : "failed";
            logger.Log(exitLevel, "Process '" + fileAndProcess + "' " + exitLabel + " with code " + result.ExitCode);
            return result;
        }

        /// <summary>
        /// Attempts to terminate the active process tree once when user cancellation reaches this command task.
        /// </summary>
        private static void TryTerminateProcessForCancellation(
            ILogger logger,
            CommandProcessState state,
            string operationTypeName)
        {
            state.WasCancelled = true;
            if (Interlocked.Exchange(ref state.CancellationTerminationRequested, 1) != 0)
            {
                logger.LogDebug("Cancellation termination for process '{FileAndProcess}' was already requested.", state.FileAndProcess);
                return;
            }

            Process? process = state.Process;
            if (process == null)
            {
                logger.LogError("Cancellation reached process-backed operation '{OperationType}' before a process instance was available. No termination attempt could be made.", operationTypeName);
                return;
            }

            int? processId = TryGetProcessId(process);
            try
            {
                if (process.HasExited)
                {
                    logger.LogInformation("Cancellation reached process '{FileAndProcess}'{ProcessIdSuffix}, but it had already exited.", state.FileAndProcess, FormatProcessIdSuffix(processId));
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                logger.LogInformation("Cancellation reached process '{FileAndProcess}'{ProcessIdSuffix}, but it had already exited.", state.FileAndProcess, FormatProcessIdSuffix(processId));
                return;
            }

            logger.LogWarning("Attempting to terminate process '{FileAndProcess}'{ProcessIdSuffix} because cancellation was requested.", state.FileAndProcess, FormatProcessIdSuffix(processId));
            try
            {
                ProcessUtils.KillProcessAndChildren(process);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Termination attempt for process '{FileAndProcess}'{ProcessIdSuffix} threw an exception.", state.FileAndProcess, FormatProcessIdSuffix(processId));
                return;
            }

            try
            {
                if (process.WaitForExit(2000))
                {
                    logger.LogInformation("Cancellation terminated process '{FileAndProcess}'{ProcessIdSuffix}.", state.FileAndProcess, FormatProcessIdSuffix(processId));
                    return;
                }

                logger.LogError("Cancellation attempted to terminate process '{FileAndProcess}'{ProcessIdSuffix}, but it is still running after 2000 ms.", state.FileAndProcess, FormatProcessIdSuffix(processId));
            }
            catch (InvalidOperationException)
            {
                logger.LogInformation("Cancellation terminated process '{FileAndProcess}'{ProcessIdSuffix}.", state.FileAndProcess, FormatProcessIdSuffix(processId));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Termination verification for process '{FileAndProcess}'{ProcessIdSuffix} failed after the kill attempt.", state.FileAndProcess, FormatProcessIdSuffix(processId));
            }
        }

        /// <summary>
        /// Holds mutable process state shared between the running task and its cancellation callback.
        /// </summary>
        private sealed class CommandProcessState(string? fileName)
        {
            /// <summary>
            /// Gets the command file leaf name used in process diagnostics.
            /// </summary>
            internal string? FileName { get; } = fileName;

            /// <summary>
            /// Gets or sets the live process instance after the command has launched.
            /// </summary>
            internal Process? Process { get; set; }

            /// <summary>
            /// Gets or sets the OS process name after launch so logs can identify the executable.
            /// </summary>
            internal string? ProcessName { get; set; }

            /// <summary>
            /// Gets or sets whether cancellation requested process termination before exit was observed.
            /// </summary>
            internal bool WasCancelled { get; set; }

            /// <summary>
            /// Stores whether the cancellation callback has already attempted process termination.
            /// </summary>
            internal int CancellationTerminationRequested;

            /// <summary>
            /// Gets a readable process identity even before launch fills in the final process name.
            /// </summary>
            internal string FileAndProcess => $"{FileName ?? "unknown-file"}:{ProcessName ?? "unknown-process"}";
        }

        /// <summary>
        /// Reads the current process identifier for diagnostics without failing when the process has already exited.
        /// </summary>
        private static int? TryGetProcessId(Process process)
        {
            try
            {
                return process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Formats the optional process identifier for cancellation logs.
        /// </summary>
        private static string FormatProcessIdSuffix(int? processId)
        {
            return processId.HasValue ? $" (pid {processId.Value})" : string.Empty;
        }
    }
}
