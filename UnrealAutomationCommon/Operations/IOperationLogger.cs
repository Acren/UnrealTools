namespace UnrealAutomationCommon.Operations
{
    public interface IOperationLogger
    {
        public void Log(string line, LogVerbosity verbosity = LogVerbosity.Log);

        public event OperationOutputEventHandler Output;
    }
}