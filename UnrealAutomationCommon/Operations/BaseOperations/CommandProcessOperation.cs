using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations;
#nullable enable

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    public abstract class CommandProcessOperation<T> : UnrealOperation<T> where T : global::LocalAutomation.Runtime.OperationTarget
    {
        private string? _fileName;
        private Process? _process;
        private string? _processName;
        private int _cancellationTerminationRequested;
        private bool _wasCancelled;

        // Process metadata is populated only after launch, so diagnostics fall back to placeholders during setup or
        // teardown paths that run before every field has been assigned.
        private string FileAndProcess => $"{_fileName ?? "unknown-file"}:{_processName ?? "unknown-process"}";

        protected abstract global::LocalAutomation.Runtime.Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters);

        // Derived operations can inspect output as it streams without retaining the full process log in memory.
        protected virtual void OnOutputLine(string line)
        {
        }

        protected override IEnumerable<global::LocalAutomation.Runtime.Command> BuildCommands(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            return new List<global::LocalAutomation.Runtime.Command> { BuildCommand(operationParameters) };
        }

        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            root.Run(ExecuteProcessAsync);
        }

        protected async Task<global::LocalAutomation.Runtime.OperationResult> ExecuteProcessAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using global::LocalAutomation.Core.PerformanceActivityScope activity = global::LocalAutomation.Core.PerformanceTelemetry.StartActivity("CommandProcessOperation.ExecuteProcess")
                .SetTag("task.id", context.TaskId.Value)
                .SetTag("task.title", context.Title)
                .SetTag("operation.type", GetType().Name);

            _wasCancelled = false;
            _cancellationTerminationRequested = 0;
            ILogger logger = context.Logger;
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters = context.ValidatedOperationParameters;
            global::LocalAutomation.Runtime.Command command;
            using (global::LocalAutomation.Core.PerformanceActivityScope buildCommandActivity = global::LocalAutomation.Core.PerformanceTelemetry.StartActivity("CommandProcessOperation.BuildCommand"))
            {
                command = BuildCommand(operationParameters);
                buildCommandActivity.SetTag("command.file", command.File)
                    .SetTag("command.has_arguments", !string.IsNullOrWhiteSpace(command.Arguments));
            }

            _fileName = Path.GetFileName(command.File);

            logger.LogInformation("Running command: " + command);

            if (!File.Exists(command.File))
            {
                throw new Exception("File " + command.File + " not found");
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
                _process = new Process { StartInfo = startInfo };
                _process.EnableRaisingEvents = true;
                _process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        HandleLogLine(logger, args.Data);
                    }
                };
                _process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        logger.LogError(args.Data);
                    }
                };
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                startProcessActivity.SetTag("process.id", _process.Id);
            }

            _processName = _process.ProcessName;
            activity.SetTag("process.name", _processName)
                .SetTag("process.file", _fileName ?? string.Empty);

            logger.LogInformation("Launched process '" + FileAndProcess + "'");

            var tcs = new TaskCompletionSource<int>();

            _process.Exited += (sender, args) =>
            {
                logger.LogDebug($"Process '{FileAndProcess}' exited");
                tcs.TrySetResult(0);
            };

            /* Register the live process-cancellation callback only after launch so terminate requests can report the
               concrete process identity they attempted to stop. */
            CancellationTokenRegistration registration = context.CancellationToken.Register(() => TryTerminateProcessForCancellation(logger));

            await tcs.Task;

            // Dispose registration, otherwise GC is prevented through above lambda
            await registration.DisposeAsync();

            return HandleProcessEnded(logger, context, operationParameters);
        }

        private void HandleLogLine(ILogger logger, string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            // Let derived operations react to streaming output without forcing the base class to store it all.
            OnOutputLine(line);

            string[] split = line.Split(new[] { ": " }, StringSplitOptions.None);
            LogLevel level = LogLevel.Information;
            if (split.Length > 1)
            {
                if (split[0] == "ERROR")
                {
                    // UBT error format
                    // "ERROR: Some message"
                    level = LogLevel.Error;
                }
                else if (split[1] == "Error")
                {
                    // Unreal error format
                    // "LogCategory: Error: Some message"
                    level = LogLevel.Error;
                }
                else if (split[1] == "Warning")
                {
                    // Unreal warning format
                    level = LogLevel.Warning;
                }
            }

            if (line.Contains("): error") || line.Contains(" : error ") || line.Contains("): fatal error") || line.Contains(" : fatal error"))
            {
                // Compiler error
                level = LogLevel.Error;
            }
            else if (line.Contains("): warning") || line.Contains(" : warning "))
            {
                // Compiler warning
                level = LogLevel.Warning;
            }

            logger.Log(level, line);
        }

        private global::LocalAutomation.Runtime.OperationResult HandleProcessEnded(ILogger logger, global::LocalAutomation.Runtime.ExecutionTaskContext context, global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            if (_process == null)
            {
                throw new InvalidOperationException("Process completion was reported before the process was started.");
            }

            global::LocalAutomation.Runtime.OperationResult result = _wasCancelled
                ? global::LocalAutomation.Runtime.OperationResult.Cancelled(_process.ExitCode)
                : _process.ExitCode == 0
                    ? global::LocalAutomation.Runtime.OperationResult.Succeeded(_process.ExitCode)
                    : global::LocalAutomation.Runtime.OperationResult.Failed(_process.ExitCode);

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
            logger.Log(exitLevel, "Process '" + FileAndProcess + "' " + exitLabel + " with code " + result.ExitCode);

            OnProcessEnded(context, operationParameters, result);

            return result;
        }

        protected virtual void OnProcessEnded(global::LocalAutomation.Runtime.ExecutionTaskContext context, global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.OperationResult result)
        {
        }

        /// <summary>
        /// Attempts to terminate the active process tree once when user cancellation reaches this operation.
        /// </summary>
        private void TryTerminateProcessForCancellation(ILogger logger)
        {
            _wasCancelled = true;
            if (Interlocked.Exchange(ref _cancellationTerminationRequested, 1) != 0)
            {
                logger.LogDebug("Cancellation termination for process '{FileAndProcess}' was already requested.", FileAndProcess);
                return;
            }

            Process? process = _process;
            if (process == null)
            {
                logger.LogError("Cancellation reached process-backed operation '{OperationType}' before a process instance was available. No termination attempt could be made.", GetType().Name);
                return;
            }

            int? processId = TryGetProcessId(process);

            try
            {
                if (process.HasExited)
                {
                    logger.LogInformation("Cancellation reached process '{FileAndProcess}'{ProcessIdSuffix}, but it had already exited.", FileAndProcess, FormatProcessIdSuffix(processId));
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                logger.LogInformation("Cancellation reached process '{FileAndProcess}'{ProcessIdSuffix}, but it had already exited.", FileAndProcess, FormatProcessIdSuffix(processId));
                return;
            }

            logger.LogWarning("Attempting to terminate process '{FileAndProcess}'{ProcessIdSuffix} because cancellation was requested.", FileAndProcess, FormatProcessIdSuffix(processId));

            try
            {
                ProcessUtils.KillProcessAndChildren(process);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Termination attempt for process '{FileAndProcess}'{ProcessIdSuffix} threw an exception.", FileAndProcess, FormatProcessIdSuffix(processId));
                return;
            }

            try
            {
                if (process.WaitForExit(2000))
                {
                    logger.LogInformation("Cancellation terminated process '{FileAndProcess}'{ProcessIdSuffix}.", FileAndProcess, FormatProcessIdSuffix(processId));
                    return;
                }

                logger.LogError("Cancellation attempted to terminate process '{FileAndProcess}'{ProcessIdSuffix}, but it is still running after 2000 ms.", FileAndProcess, FormatProcessIdSuffix(processId));
            }
            catch (InvalidOperationException)
            {
                logger.LogInformation("Cancellation terminated process '{FileAndProcess}'{ProcessIdSuffix}.", FileAndProcess, FormatProcessIdSuffix(processId));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Termination verification for process '{FileAndProcess}'{ProcessIdSuffix} failed after the kill attempt.", FileAndProcess, FormatProcessIdSuffix(processId));
            }
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
        /// Formats the optional process identifier as a suffix so termination logs stay readable when the process has
        /// already exited and the runtime can no longer read its PID.
        /// </summary>
        private static string FormatProcessIdSuffix(int? processId)
        {
            return processId.HasValue ? $" (pid {processId.Value})" : string.Empty;
        }
    }
}
