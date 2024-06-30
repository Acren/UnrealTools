using Microsoft.Extensions.Logging;
using UnrealAutomationCommon;

namespace UnrealCommander
{
    public class LogEntry
    {
        public string Message { get; set; }
        public LogLevel Verbosity { get; set; }
    }
}
