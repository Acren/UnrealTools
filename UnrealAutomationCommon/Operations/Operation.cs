using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon.Operations
{
    public abstract class Operation
    {
        public static Operation CreateOperation(Type operationType)
        {
            Operation instance = (Operation)Activator.CreateInstance(operationType);
            return instance;
        }

        public static bool OperationTypeSupportsTarget(Type operationType, OperationTarget target)
        {
            return CreateOperation(operationType).SupportsTarget(target);
        }

        public string OperationName => GetOperationName();

        public Process Execute(OperationParameters operationParameters, DataReceivedEventHandler outputHandler, DataReceivedEventHandler errorHandler, EventHandler exitHandler )
        {
            if (!RequirementsSatisfied(operationParameters))
            {
                throw new Exception("Requirements not satisfied");
            }

            Command command = BuildCommand(operationParameters);

            if (command == null)
            {
                throw new Exception("No command");
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = command.File,
                Arguments = command.Arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process process = new Process { StartInfo = startInfo };
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += errorHandler;
            process.Exited += exitHandler;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return process;
        }

        public Command GetCommand(OperationParameters operationParameters)
        {
            if (!RequirementsSatisfied(operationParameters))
            {
                return null;
            }

            return BuildCommand(operationParameters);
        }

        public virtual bool SupportsConfiguration(BuildConfiguration configuration)
        {
            return true;
        }

        public virtual bool RequirementsSatisfied(OperationParameters operationParameters)
        {
            if (!SupportsTarget(operationParameters.Target))
            {
                return false;
            }

            if (!SupportsConfiguration(operationParameters.Configuration))
            {
                return false;
            }

            if (!GetRelevantEngineInstall(operationParameters).SupportsConfiguration(operationParameters.Configuration))
            {
                return false;
            }

            return true;
        }

        protected string GetTargetName(OperationParameters operationParameters)
        {
            return operationParameters.Target.GetName();
        }

        public string GetOutputPath(OperationParameters operationParameters)
        {
            string path = operationParameters.OutputPathRoot;
            if (operationParameters.UseOutputPathProjectSubfolder)
            {
                string subfolderName = GetTargetName(operationParameters);
                path = Path.Combine(path, subfolderName.Replace(" ", ""));
            }
            if (operationParameters.UseOutputPathOperationSubfolder)
            {
                path = Path.Combine(path, OperationName.Replace(" ",""));
            }
            return path;
        }

        protected abstract Command BuildCommand(OperationParameters operationParameters );

        protected virtual string GetOperationName()
        {
            string name = GetType().Name;
            return name.SplitWordsByUppercase();
        }

        public EngineInstall GetRelevantEngineInstall(OperationParameters operationParameters)
        {
            return operationParameters.Target?.GetEngineInstall();
        }

        public abstract bool SupportsTarget(OperationTarget Target);
    }
}
