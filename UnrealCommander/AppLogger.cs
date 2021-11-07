using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;

namespace UnrealCommander
{
    class AppLogger : SingletonBase<AppLogger>, ILogger
    {
        public void Log(string line, LogVerbosity verbosity = LogVerbosity.Log)
        {
            Output?.Invoke(line, verbosity);
        }

        public event LogEventHandler Output;
    }
}
