using System;
using System.Diagnostics;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides lightweight section logging helpers for shared runtime flows.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Writes a visible section header into the active logger.
    /// </summary>
    public static void LogSectionHeader(this ILogger logger, string header)
    {
        logger.LogInformation(string.Empty);
        logger.LogInformation("####################################");
        logger.LogInformation($"# {header}");
        logger.LogInformation("####################################");
    }

    /// <summary>
    /// Starts a timed logging section and reports its total duration on disposal.
    /// </summary>
    public static IDisposable BeginSection(this ILogger logger, string header)
    {
        logger.LogSectionHeader(header);
        return new SectionTimer(logger, header);
    }

    /// <summary>
    /// Captures elapsed time for a single logged section.
    /// </summary>
    private sealed class SectionTimer : IDisposable
    {
        private readonly string _header;
        private readonly ILogger _logger;
        private readonly Stopwatch _stopwatch;

        /// <summary>
        /// Starts timing for the provided section header.
        /// </summary>
        public SectionTimer(ILogger logger, string header)
        {
            _logger = logger;
            _header = header;
            _stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Stops timing and emits the final duration log entry.
        /// </summary>
        public void Dispose()
        {
            _stopwatch.Stop();
            _logger.LogInformation("Finished section '{SectionHeader}' in {Elapsed}.", _header, DurationFormatting.FormatSeconds(_stopwatch.Elapsed));
        }
    }
}
