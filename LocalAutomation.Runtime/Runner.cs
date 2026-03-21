using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Coordinates lifecycle, cancellation, and high-level logging for a runtime operation execution.
/// </summary>
public class Runner
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly OperationParameters _operationParameters;
    private readonly Action<string>? _deleteDirectoryIfExists;
    private readonly Action<OperationParameters, ILogger>? _beforeRun;
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();
    private Task<OperationResult>? _currentTask;
    private ILogger _logger = ApplicationLogger.Logger;

    /// <summary>
    /// Creates a runtime runner for the provided operation and parameters.
    /// </summary>
    public Runner(Operation operation, OperationParameters operationParameters, Action<string>? deleteDirectoryIfExists = null, Action<OperationParameters, ILogger>? beforeRun = null)
    {
        Operation = operation;
        _operationParameters = operationParameters;
        _deleteDirectoryIfExists = deleteDirectoryIfExists;
        _beforeRun = beforeRun;
    }

    /// <summary>
    /// Gets whether the runner currently has an active operation task.
    /// </summary>
    public bool IsRunning => _currentTask is { IsCompleted: false };

    /// <summary>
    /// Gets the operation associated with this runner.
    /// </summary>
    public Operation Operation { get; }

    /// <summary>
    /// Executes the current operation and returns the final runtime result.
    /// </summary>
    public async Task<OperationResult> Run()
    {
        if (_currentTask != null)
        {
            throw new Exception("Task is already running");
        }

        string outputPath = Operation.GetOutputPath(_operationParameters);
        _deleteDirectoryIfExists?.Invoke(outputPath);
        _beforeRun?.Invoke(_operationParameters, _logger);

        EventStreamLogger eventLogger = new();
        eventLogger.Output += (level, output) =>
        {
            if (output == null)
            {
                throw new Exception("Null line");
            }

            if (level >= LogLevel.Error)
            {
                _errors.Add(output);
            }
            else if (level == LogLevel.Warning)
            {
                _warnings.Add(output);
            }

            _logger.Log(level, output);
        };

        _currentTask = Operation.ExecuteOnThread(_operationParameters, eventLogger, _cancellationTokenSource.Token);
        OperationResult result = await _currentTask.ConfigureAwait(false);
        _logger.LogDebug($"'{Operation.OperationName}' task ended");
        _currentTask = null;

        if (result.Success)
        {
            if (_warnings.Count > 0)
            {
                int numToShow = Math.Min(_warnings.Count, 50);
                _logger.LogWarning($"{numToShow} of {_warnings.Count} warnings:");
                foreach (string warning in _warnings.Take(numToShow))
                {
                    _logger.LogWarning(warning);
                }
            }

            if (_errors.Count > 0)
            {
                int numToShow = Math.Min(_errors.Count, 50);
                _logger.LogWarning($"{numToShow} of {_errors.Count} errors:");
                foreach (string error in _errors.Take(numToShow))
                {
                    _logger.LogError(error);
                }
            }

            _logger.LogInformation($"'{Operation.OperationName}' finished with result: success");
        }
        else
        {
            _logger.LogError($"'{Operation.OperationName}' finished with result: failure");
        }

        return result;
    }

    /// <summary>
    /// Cancels the current operation and waits for it to stop.
    /// </summary>
    public async Task Cancel()
    {
        if (_currentTask == null)
        {
            throw new Exception("Task is not running");
        }

        _logger.LogWarning($"Cancelling operation '{Operation.OperationName}'");
        _cancellationTokenSource.Cancel();
        await _currentTask.ConfigureAwait(false);
        _logger.LogWarning($"'{Operation.OperationName}' task ended from cancellation");
    }
}
