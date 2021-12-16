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
        private string _fileName;
        private Process _process;
        private string _processName;

        private string FileAndProcess => $"{_fileName}:{_processName}";

        protected abstract Command BuildCommand(OperationParameters operationParameters);

        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            return new List<Command> { BuildCommand(operationParameters) };
        }

        protected override async Task<OperationResult> OnExecuted(CancellationToken token)
        {
            Command command = BuildCommand(OperationParameters);

            _fileName = Path.GetFileName(command.File);

            Logger.Log("Running command: " + command);

            if (command == null)
            {
                throw new Exception("No command");
            }

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
            _process.OutputDataReceived += (sender, args) => { HandleLogLine(args.Data); };
            _process.ErrorDataReceived += (sender, args) => { Logger.Log(args.Data, LogVerbosity.Error); };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _processName = _process.ProcessName;

            Logger.Log("Launched process '" + FileAndProcess + "'");

            var tcs = new TaskCompletionSource<int>();

            _process.Exited += (sender, args) =>
            {
                Logger.Log($"Process '{FileAndProcess}' exited");
                tcs.TrySetResult(0);
            };

            CancellationTokenRegistration registration = token.Register(async () =>
            {
                // Token cancelled, kill the process
                Logger.Log($"Operation '{OperationName}' cancelled", LogVerbosity.Warning);
                Logger.Log("Terminating process '" + FileAndProcess + "'", LogVerbosity.Warning);
                SetCancelled();
                await Task.Run(() => ProcessUtils.KillProcessAndChildren(_process));
            });

            await tcs.Task;

            // Dispose registration, otherwise GC is prevented through above lambda
            await registration.DisposeAsync();

            return HandleProcessEnded();
        }

        private void HandleLogLine(string line)
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

            if (line.Contains("): error"))
            {
                // Compiler error
                verbosity = LogVerbosity.Error;
            }
            else if (line.Contains("): warning"))
            {
                // Compiler warning
                verbosity = LogVerbosity.Warning;
            }

            Logger.Log(line, verbosity);
        }

        private OperationResult HandleProcessEnded()
        {
            bool success = _process.ExitCode == 0 && !Cancelled;
            OperationResult result = new(success)
            {
                ExitCode = _process.ExitCode
            };

            Logger.Log("Process '" + FileAndProcess + "' exited with code " + result.ExitCode, success ? LogVerbosity.Log : LogVerbosity.Error);

            OnProcessEnded(result);

            return result;
        }

        protected virtual void OnProcessEnded(OperationResult result)
        {
        }
    }
}