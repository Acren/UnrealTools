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
            }
            return null;
        }

        public string OperationName
        {
            get { return GetOperationName(); }
        }

        public void Execute(OperationParameters operationParameters )
        {
            RunProcess.Run(GetCommand(operationParameters));
        }

        public abstract Command GetCommand(OperationParameters operationParameters );

        public virtual string GetOperationName()
        {
            return "Execute";
        }
    }
}
