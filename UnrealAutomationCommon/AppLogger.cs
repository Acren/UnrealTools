namespace UnrealAutomationCommon
{
    public class AppLogger : SingletonBase<AppLogger>, ILogger
    {
        public void Log(string line, LogVerbosity verbosity = LogVerbosity.Log)
        {
            Output?.Invoke(line, verbosity);
        }

        public event LogEventHandler Output;
    }
}
