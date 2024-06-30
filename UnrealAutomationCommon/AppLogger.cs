using Microsoft.Extensions.Logging;

namespace UnrealAutomationCommon
{
    public class AppLogger : SingletonBase<AppLogger>
    {
        public ILogger Logger { get; set; }

        public static ILogger LoggerInstance => Instance.Logger;
    }
}
