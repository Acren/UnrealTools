using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public abstract class Operation
    {
        public string OperationName => GetOperationName();


        public static Operation CreateOperation(Type operationType)
        {
            Operation instance = (Operation)Activator.CreateInstance(operationType);
            return instance;
        }

        public static bool OperationTypeSupportsTarget(Type operationType, OperationTarget target)
        {
            return CreateOperation(operationType).SupportsTarget(target);
        }


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

            if (!File.Exists(command.File))
            {
                throw new Exception("File " + command.File + " not found");
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
                path = Path.Combine(path, OperationName.Replace(" ", ""));
            }
            return path;
        }

        public EngineInstall GetRelevantEngineInstall(OperationParameters operationParameters)
        {
            return operationParameters.Target?.GetEngineInstall();
        }

        public List<Type> GetRequiredOptionSetTypes(OperationTarget target)
        {
            if (target == null)
            {
                return null;
            }
            List<Type> result = new ();
            OperationParameters dummyParams = new();
            dummyParams.Target = target;
            Command command = BuildCommand(dummyParams);
            foreach (OperationOptions options in dummyParams.OptionsInstances)
            {
                result.Add(options.GetType());
            }
            return result;
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

            BuildConfigurationOptions options = operationParameters.FindOptions<BuildConfigurationOptions>();

            if (options != null)
            {
                if (!SupportsConfiguration(options.Configuration))
                {
                    return false;
                }

                if (!GetRelevantEngineInstall(operationParameters).SupportsConfiguration(options.Configuration))
                {
                    return false;
                }
            }

            return true;
        }

        public virtual bool ShouldReadOutputFromLogFile()
        {
            return false;
        }

        public abstract bool SupportsTarget(OperationTarget Target);

        public virtual string GetLogsPath(OperationParameters operationParameters)
        {
            return null;
        }

        protected abstract Command BuildCommand(OperationParameters operationParameters);

        protected string GetTargetName(OperationParameters operationParameters)
        {
            return operationParameters.Target.GetName();
        }

        protected virtual string GetOperationName()
        {
            string name = GetType().Name;
            return name.SplitWordsByUppercase();
        }


    }
}
