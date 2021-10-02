using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Operations
{
    public delegate void OperationOutputEventHandler(string output, LogVerbosity verbosity);

    public class OperationRunner : IOperationLogger
    {
        public Operation Operation { get; private set; }
        private OperationParameters _operationParameters;
        private int _lineCount = 0;

        public event OperationOutputEventHandler Output;

        public OperationRunner(Operation operation, OperationParameters operationParameters)
        {
            Operation = operation;
            _operationParameters = operationParameters;
        }

        public async Task<OperationResult> Run()
        {
            string outputPath = Operation.GetOutputPath(_operationParameters);
            FileUtils.DeleteDirectoryIfExists(outputPath);

            Task<OperationResult> task = Operation.Execute(_operationParameters, this);

            FlagOptions flagOptions = _operationParameters.FindOptions<FlagOptions>();
            if (flagOptions.WaitForAttach)
            {
                Output?.Invoke("-WaitForAttach was specified, attach now", LogVerbosity.Log);
            }

            return await task;
        }

        public void Terminate()
        {
            Output?.Invoke("Terminating operation '" + Operation.OperationName + "'", LogVerbosity.Warning);
            Operation.Terminate();
        }

        void OutputLine(string line, LogVerbosity verbosity)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            _lineCount++;
            Output?.Invoke("[" + _lineCount + "]: " + line, verbosity);
        }

        public void Log(string line, LogVerbosity verbosity)
        {
            OutputLine(line, verbosity);
        }
    }
}
