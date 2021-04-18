using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnrealAutomationCommon.Operations.OperationTypes;

namespace UnrealAutomationCommon.Operations
{
    public abstract class Operation
    {
        public static Operation CreateOperation(Type operationType)
        {
            Operation instance = (Operation)Activator.CreateInstance(operationType);
            return instance;
        }

        public string OperationName => GetOperationName();

        public Process Execute(OperationParameters operationParameters, DataReceivedEventHandler outputHandler, EventHandler exitHandler )
        {
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
            process.ErrorDataReceived += outputHandler;
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

        protected string GetOutputPath(OperationParameters operationParameters)
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
            return string.Concat(name.Select(x => Char.IsUpper(x) ? " " + x : x.ToString())).TrimStart(' ');
        }

        public EngineInstall GetRelevantEngineInstall(OperationParameters operationParameters)
        {
            return operationParameters.Target?.GetEngineInstall();
        }
    }
}
