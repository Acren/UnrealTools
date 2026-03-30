using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Coordinates lifecycle, cancellation, and high-level logging for a runtime operation execution.
/// </summary>
public class Runner
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly OperationParameters _operationParameters;
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();
    private Task<OperationResult>? _currentTask;
    private ILogger _logger = ApplicationLogger.Logger;

    /// <summary>
    /// Creates a runtime runner for the provided operation and parameters.
    /// </summary>
    public Runner(Operation operation, OperationParameters operationParameters)
    {
        Operation = operation;
        _operationParameters = operationParameters;
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

        // Resolve the logger at execution time instead of construction time so the current host can swap in a
        // session-specific forwarding logger before the run starts.
        try
        {
            _logger = ApplicationLogger.Logger;
        }
        catch (InvalidOperationException)
        {
            _logger = NullLogger.Instance;
        }

        string outputPath = Operation.GetOutputPath(_operationParameters);
        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }

        Operation.PrepareForExecution(_operationParameters, _logger);

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

        if (result.Outcome == RunOutcome.Succeeded)
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
        else if (result.Outcome == RunOutcome.Cancelled)
        {
            _logger.LogWarning($"'{Operation.OperationName}' finished with result: cancelled");
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
        Task<OperationResult>? currentTask = _currentTask;
        if (currentTask == null)
        {
            return;
        }

        _logger.LogWarning($"Cancelling operation '{Operation.OperationName}'");
        _cancellationTokenSource.Cancel();
        await currentTask.ConfigureAwait(false);
    }
}
