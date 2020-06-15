using System;

namespace UnrealAutomationCommon.Operations
{
    public abstract class Operation
    {
        public static Operation CreateOperation(OperationType operationType)
        {
            switch (operationType)
            {
                case OperationType.BuildEditor:
                    return new BuildEditor();
                case OperationType.OpenEditor:
                    return new OpenEditor();
                case OperationType.PackageProject:
                    return new PackageProject();
                default:
                    throw new ArgumentOutOfRangeException(nameof(operationType), operationType, null);
            }
        }

        public string OperationName => GetOperationName();

        public void Execute(OperationParameters operationParameters )
        {
            RunProcess.Run(GetCommand(operationParameters));
        }

        public abstract Command GetCommand(OperationParameters operationParameters );

        protected virtual string GetOperationName()
        {
            return "Execute";
        }
    }
}
