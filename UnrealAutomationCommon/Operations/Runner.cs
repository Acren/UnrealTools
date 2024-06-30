using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Operations
{
    public class Runner
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly OperationParameters _operationParameters;
        private int _lineCount;

        private Task<OperationResult> _currentTask = null;
        private List<string> _errors = new();
        private List<string> _warnings = new();

        private ILogger logger = AppLogger.LoggerInstance;

        public Runner(Operation operation, OperationParameters operationParameters)
        {
            Operation = operation;
            _operationParameters = operationParameters;
        }

        public bool IsRunning => _currentTask is { IsCompleted: false };

        public Operation Operation { get; }

        public async Task<OperationResult> Run()
        {
            if (_currentTask != null)
            {
                throw new Exception("Task is already running");
            }

            string outputPath = Operation.GetOutputPath(_operationParameters);
            FileUtils.DeleteDirectoryIfExists(outputPath);
            
            EventLogger eventLogger = new();
            eventLogger.Output += (level, output) =>
            {
                if (output == null)
                {
                    throw new Exception("Null line");
                }

                _lineCount++;

                if (level >= LogLevel.Error)
                {
                    _errors.Add(output);
                }
                else if (level == LogLevel.Warning)
                {
                    _warnings.Add(output);
                }
                
                logger.Log(level, output);
            };

            _currentTask = Operation.ExecuteOnThread(_operationParameters, eventLogger, _cancellationTokenSource.Token);

            FlagOptions flagOptions = _operationParameters.FindOptions<FlagOptions>();
            if (flagOptions is { WaitForAttach: true })
            {
                logger.LogInformation("-WaitForAttach was specified, attach now");
            }

            OperationResult result = await _currentTask;
            logger.LogInformation($"'{Operation.OperationName}' task ended");
            _currentTask = null;

            // Log error and warning summary

            if (_warnings.Count > 0)
            {
                logger.LogWarning("Warnings:");
                foreach (string warning in _warnings)
                {
                    logger.LogWarning(warning);
                }
            }

            if (_errors.Count > 0)
            {
                logger.LogError("Errors:");
                foreach (string error in _errors)
                {
                    logger.LogError(error);
                }
            }

            logger.LogInformation($"'{Operation.OperationName}' finished running");

            return result;
        }

        public async Task Cancel()
        {
            if (_currentTask == null)
            {
                throw new Exception("Task is not running");
            }

            logger.LogWarning($"Cancelling operation '{Operation.OperationName}'");

            _cancellationTokenSource.Cancel();

            await _currentTask;

            logger.LogWarning($"'{Operation.OperationName}' task ended from cancellation");
        }
    }
}