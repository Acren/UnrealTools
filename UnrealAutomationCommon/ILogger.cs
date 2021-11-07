namespace UnrealAutomationCommon
{
    public delegate void LogEventHandler(string output, LogVerbosity verbosity);

    public interface ILogger
    {
        public void Log(string line, LogVerbosity verbosity = LogVerbosity.Log);

        public event LogEventHandler Output;
    }
}