namespace UnrealAutomationCommon
{
    public interface IOperationLogger
    {
        public void Log(string line, LogVerbosity verbosity = LogVerbosity.Log);
    }
}
