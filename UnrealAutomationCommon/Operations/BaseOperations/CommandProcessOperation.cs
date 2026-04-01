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

        protected abstract global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters);

        // Derived operations can inspect output as it streams without retaining the full process log in memory.
        protected virtual void OnOutputLine(string line)
        {
        }

        protected override IEnumerable<global::LocalAutomation.Runtime.Command> BuildCommands(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            return new List<global::LocalAutomation.Runtime.Command> { BuildCommand((UnrealAutomationCommon.Operations.UnrealOperationParameters)operationParameters) };
        }

        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.OperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            root.Run(ExecuteProcessAsync);
        }

        protected async Task<global::LocalAutomation.Runtime.OperationResult> ExecuteProcessAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            _wasCancelled = false;
            ILogger logger = context.Logger;
            UnrealOperationParameters unrealOperationParameters = GetUnrealOperationParameters(context);
            global::LocalAutomation.Runtime.Command command = BuildCommand(unrealOperationParameters);

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

            _process = new Process { StartInfo = startInfo };
            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += (sender, args) => { HandleLogLine(logger, args.Data); };
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

            _processName = _process.ProcessName;

            logger.LogInformation("Launched process '" + FileAndProcess + "'");

            var tcs = new TaskCompletionSource<int>();

            _process.Exited += (sender, args) =>
            {
                logger.LogDebug($"Process '{FileAndProcess}' exited");
                tcs.TrySetResult(0);
            };

            CancellationTokenRegistration registration = context.CancellationToken.Register(() => TryTerminateProcessForCancellation());

            await tcs.Task;

            // Dispose registration, otherwise GC is prevented through above lambda
            await registration.DisposeAsync();

            return HandleProcessEnded(logger, context, unrealOperationParameters);
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

        private global::LocalAutomation.Runtime.OperationResult HandleProcessEnded(ILogger logger, global::LocalAutomation.Runtime.ExecutionTaskContext context, UnrealOperationParameters operationParameters)
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

            LogLevel exitLevel = result.Outcome == global::LocalAutomation.Runtime.RunOutcome.Cancelled
                ? LogLevel.Warning
                : result.Outcome == global::LocalAutomation.Runtime.RunOutcome.Succeeded
                    ? LogLevel.Information
                    : LogLevel.Error;
            string exitLabel = result.Outcome == global::LocalAutomation.Runtime.RunOutcome.Cancelled
                ? "cancelled"
                : result.Outcome == global::LocalAutomation.Runtime.RunOutcome.Succeeded
                    ? "succeeded"
                    : "failed";
            logger.Log(exitLevel, "Process '" + FileAndProcess + "' " + exitLabel + " with code " + result.ExitCode);

            OnProcessEnded(context, operationParameters, result);

            return result;
        }

        protected virtual void OnProcessEnded(global::LocalAutomation.Runtime.ExecutionTaskContext context, UnrealOperationParameters operationParameters, global::LocalAutomation.Runtime.OperationResult result)
        {
        }

        /// <summary>
        /// Attempts to terminate the active process tree once when user cancellation reaches this operation.
        /// </summary>
        private void TryTerminateProcessForCancellation()
        {
            _wasCancelled = true;
            if (Interlocked.Exchange(ref _cancellationTerminationRequested, 1) != 0)
            {
                return;
            }

            Process? process = _process;
            if (process == null)
            {
                return;
            }

            try
            {
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            ProcessUtils.KillProcessAndChildren(process);
        }
    }
}
