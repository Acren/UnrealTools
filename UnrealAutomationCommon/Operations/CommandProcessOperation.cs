using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Operations
{
    public abstract class CommandProcessOperation<T> : Operation<T> where T : OperationTarget
    {
        private Process _process = null;
        private IOperationLogger _logger = null;
        private string _processName = null;
        private OperationParameters _operationParameters = null;

        protected abstract Command BuildCommand(OperationParameters operationParameters);

        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            return new List<Command>() { BuildCommand(operationParameters) };
        }

        protected override void OnExecuted(OperationParameters operationParameters, IOperationLogger logger)
        {
            _operationParameters = operationParameters;
            _logger = logger;

            Command command = BuildCommand(operationParameters);

            _logger.Log("Running command: " + command, LogVerbosity.Log);

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
                logger.Log(args.Data, LogVerbosity.Error);
            };
            _process.Exited += (sender, args) =>
            {
                HandleProcessEnded();
            };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _processName = _process.ProcessName;

            logger.Log("Launched process '" + _processName + "'", LogVerbosity.Log);
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
            _logger.Log(line, verbosity);
        }

        void HandleProcessEnded()
        {
            OperationResult result = new OperationResult();
            result.ExitCode = _process.ExitCode;

            _logger.Log("Process '" + _processName + "' exited with code " + result.ExitCode, result.ExitCode == 0 ? LogVerbosity.Log : LogVerbosity.Error);

            AutomationOptions automationOptions = _operationParameters.FindOptions<AutomationOptions>();
            if (automationOptions.RunTests)
            {
                string reportFilePath = OutputPaths.GetTestReportFilePath(GetOutputPath(_operationParameters));
                TestReport report = TestReport.Load(reportFilePath);
                if (report != null)
                {
                    result.TestReport = report;
                }
                else
                {
                    _logger.Log("Expected test report at " + reportFilePath + " but didn't find one", LogVerbosity.Error);
                }

                if (result.TestReport != null)
                {
                    foreach (Test test in result.TestReport.Tests)
                    {
                        _logger.Log(EnumUtils.GetName(test.State).ToUpperInvariant().PadRight(7) + " - " + test.FullTestPath, test.State == TestState.Success ? LogVerbosity.Log : LogVerbosity.Error);
                        foreach (TestEntry entry in test.Entries)
                        {
                            if (entry.Event.Type != TestEventType.Info)
                            {
                                _logger.Log("".PadRight(9) + " - " + entry.Event.Message, entry.Event.Type == TestEventType.Error ? LogVerbosity.Error : LogVerbosity.Warning);
                            }
                        }
                    }
                    int testsPassed = result.TestReport.Tests.Count(t => t.State == TestState.Success);
                    bool allPassed = testsPassed == result.TestReport.Tests.Count;
                    _logger.Log(testsPassed + " of " + result.TestReport.Tests.Count + " tests passed", allPassed ? LogVerbosity.Log : LogVerbosity.Error);
                }
            }

            _logger.Log("Operation '" + OperationName + "' " + (_terminated ? "terminated" : "completed"), _terminated ? LogVerbosity.Warning : LogVerbosity.Log);

            End(result);
        }

        protected override void OnTerminated()
        {
            ProcessUtils.KillProcessAndChildren(_process);
        }
    }
}
