using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Operations
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

        protected override async Task<OperationResult> OnExecuted()
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

            await tcs.Task;

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
            Logger.Log(line, verbosity);
        }

        OperationResult HandleProcessEnded()
        {
            OperationResult result = new OperationResult(_process.ExitCode == 0);
            result.ExitCode = _process.ExitCode;

            Logger.Log("Process '" + _processName + "' exited with code " + result.ExitCode, result.ExitCode == 0 ? LogVerbosity.Log : LogVerbosity.Error);

            AutomationOptions automationOptions = OperationParameters.FindOptions<AutomationOptions>();
            if (automationOptions is { RunTests: true })
            {
                string reportFilePath = OutputPaths.GetTestReportFilePath(GetOutputPath(OperationParameters));
                TestReport report = TestReport.Load(reportFilePath);
                if (report != null)
                {
                    result.TestReport = report;
                }
                else
                {
                    throw new Exception("Expected test report at " + reportFilePath + " but didn't find one");
                }

                if (result.TestReport != null)
                {
                    foreach (Test test in result.TestReport.Tests)
                    {
                        Logger.Log(EnumUtils.GetName(test.State).ToUpperInvariant().PadRight(7) + " - " + test.FullTestPath, test.State == TestState.Success ? LogVerbosity.Log : LogVerbosity.Error);
                        foreach (TestEntry entry in test.Entries)
                        {
                            if (entry.Event.Type != TestEventType.Info)
                            {
                                Logger.Log("".PadRight(9) + " - " + entry.Event.Message, entry.Event.Type == TestEventType.Error ? LogVerbosity.Error : LogVerbosity.Warning);
                            }
                        }
                    }
                    int testsPassed = result.TestReport.Tests.Count(t => t.State == TestState.Success);
                    bool allPassed = testsPassed == result.TestReport.Tests.Count;
                    Logger.Log(testsPassed + " of " + result.TestReport.Tests.Count + " tests passed", allPassed ? LogVerbosity.Log : LogVerbosity.Error);
                }

                if (report.Failed > 0)
                {
                    throw new Exception("Tests failed");
                }
            }

            return result;
        }

        protected override void OnTerminated()
        {
            ProcessUtils.KillProcessAndChildren(_process);
        }
    }
}
