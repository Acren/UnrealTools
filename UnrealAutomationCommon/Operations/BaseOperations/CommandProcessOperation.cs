using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    public abstract class CommandProcessOperation<T> : Operation<T> where T : OperationTarget
    {
        private Process _process = null;
        private string _processName = null;

        protected abstract Command BuildCommand(OperationParameters operationParameters);

        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            return new List<Command>() { BuildCommand(operationParameters) };
        }

        protected override async Task<OperationResult> OnExecuted(CancellationToken token)
        {
            Command command = BuildCommand(OperationParameters);

            Logger.Log("Running command: " + command, LogVerbosity.Log);

            if (command == null)
            {
                throw new Exception("No command");
            }

            if (!File.Exists(command.File))
            {
                throw new Exception("File " + command.File + " not found");
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
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
            _process.OutputDataReceived += (sender, args) =>
            {
                HandleLogLine(args.Data);
            };
            _process.ErrorDataReceived += (sender, args) =>
            {
                Logger.Log(args.Data, LogVerbosity.Error);
            };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _processName = _process.ProcessName;

            Logger.Log("Launched process '" + _processName + "'", LogVerbosity.Log);

            var tcs = new TaskCompletionSource<int>();

            _process.Exited += (sender, args) =>
            {
                tcs.TrySetResult(0);
            };

            CancellationTokenRegistration registration = token.Register(() =>
            {
                // Token cancelled, kill the process
                Logger.Log($"Operation '{OperationName}' cancelled", LogVerbosity.Warning);
                Logger.Log("Terminating process '" + _processName + "'", LogVerbosity.Warning);
                SetCancelled();
                ProcessUtils.KillProcessAndChildren(_process);
            });

            await tcs.Task;

            // Dispose registration, otherwise GC is prevented through above lambda
            await registration.DisposeAsync();

            return HandleProcessEnded();
        }

        void HandleLogLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            string[] split = line.Split(new[] { ": " }, StringSplitOptions.None);
            LogVerbosity verbosity = LogVerbosity.Log;
            if (split.Length > 1)
            {
                if (split[0] == "ERROR")
                {
                    // UBT error format
                    // "ERROR: Some message"
                    verbosity = LogVerbosity.Error;
                }
                else if (split[1] == "Error")
                {
                    // Unreal error format
                    // "LogCategory: Error: Some message"
                    verbosity = LogVerbosity.Error;
                }
                else if (split[1] == "Warning")
                {
                    // Unreal warning format
                    verbosity = LogVerbosity.Warning;
                }
            }

            if (line.Contains("): warning "))
            {
                // MSBuild warning format
                verbosity = LogVerbosity.Warning;
            }

            Logger.Log(line, verbosity);
        }

        OperationResult HandleProcessEnded()
        {
            bool success = _process.ExitCode == 0;
            OperationResult result = new(success)
            {
                ExitCode = _process.ExitCode
            };

            Logger.Log("Process '" + _processName + "' exited with code " + result.ExitCode, success ? LogVerbosity.Log : LogVerbosity.Error);

            OnProcessEnded(result);

            return result;
        }

        protected virtual void OnProcessEnded(OperationResult result)
        {
        }

    }
}
