using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace UnrealAutomationCommon
{
    public static class LoggingExtensions
    {
        // Keep section headers visually prominent while returning a timed scope that reports the total duration when
        // the caller exits the section.
        public static void LogSectionHeader(this ILogger logger, string header)
        {
            logger.LogInformation("");
            logger.LogInformation("####################################");
            logger.LogInformation($"# {header}");
            logger.LogInformation("####################################");
        }

        // Return a lightweight scope that logs the elapsed time with the section name when the section completes.
        public static IDisposable BeginSection(this ILogger logger, string header)
        {
            logger.LogSectionHeader(header);

            return new SectionTimer(logger, header);
        }

        // Keep the timing output explicit so long-running UAT logs always show which section just finished.
        private sealed class SectionTimer : IDisposable
        {
            private readonly ILogger _logger;
            private readonly string _header;
            private readonly Stopwatch _stopwatch;

            public SectionTimer(ILogger logger, string header)
            {
                _logger = logger;
                _header = header;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _stopwatch.Stop();
                _logger.LogInformation("Finished section '{SectionHeader}' in {ElapsedSeconds:0.00} s", _header, _stopwatch.Elapsed.TotalSeconds);
            }
        }
    }
}
