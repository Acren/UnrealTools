using Microsoft.Extensions.Logging;

namespace UnrealAutomationCommon
{
    public static class LoggingExtensions
    {
        public static void LogSectionHeader(this ILogger logger, string header)
        {
            logger.LogInformation("");
            logger.LogInformation("####################################");
            logger.LogInformation($"# {header}");
            logger.LogInformation("####################################");
        }
    }
}