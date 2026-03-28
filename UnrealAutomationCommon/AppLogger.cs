using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace UnrealAutomationCommon
{
    public class AppLogger : SingletonBase<AppLogger>
    {
        // A no-op default logger keeps background utility classes safe before app startup wires the real logger.
        public ILogger Logger { get; set; } = NullLogger.Instance;

        public static ILogger LoggerInstance => Instance.Logger;
    }
}
