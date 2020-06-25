using System;
using System.IO;
using UnrealAutomationCommon.Operations.OperationTypes;

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
                case OperationType.BuildPlugin:
                    return new BuildPlugin();
                default:
                    throw new ArgumentOutOfRangeException(nameof(operationType), operationType, null);
            }
        }

        public string OperationName => GetOperationName();

        public void Execute(OperationParameters operationParameters )
        {
            Command command = BuildCommand(operationParameters);
            if (command != null)
            {
                RunProcess.Run(command);
            }
        }

        public Command GetCommand(OperationParameters operationParameters)
        {
            if (!RequirementsSatisfied(operationParameters))
            {
                return null;
            }

            return BuildCommand(operationParameters);
        }

        public bool RequirementsSatisfied(OperationParameters operationParameters)
        {
            if (RequiresProject() && operationParameters.Project == null)
            {
                return false;
            }

            if (RequiresPlugin() && operationParameters.Plugin == null)
            {
                return false;
            }

            return true;
        }

        protected string GetOutputPath(OperationParameters operationParameters)
        {
            string path = operationParameters.OutputPathRoot;
            if (operationParameters.UseOutputPathProjectSubfolder)
            {
                string subfolderName = IsPluginOnlyOperation()
                    ? operationParameters.Plugin.Name
                    : operationParameters.Project.Name;
                path = Path.Combine(path, subfolderName.Replace(" ", ""));
            }
            if (operationParameters.UseOutputPathOperationSubfolder)
            {
                path = Path.Combine(path, OperationName.Replace(" ",""));
            }
            return path;
        }

        protected abstract Command BuildCommand(OperationParameters operationParameters );

        protected virtual bool RequiresProject()
        {
            return !IsPluginOnlyOperation();
        }

        protected virtual bool RequiresPlugin()
        {
            return IsPluginOnlyOperation();
        }

        protected virtual bool IsPluginOnlyOperation()
        {
            return false;
        }

        protected virtual string GetOperationName()
        {
            return "Execute";
        }
    }
}
