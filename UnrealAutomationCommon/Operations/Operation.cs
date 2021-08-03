﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public abstract class Operation
    {
        public string OperationName => GetOperationName();

        protected bool Terminated { get; private set; }
        protected IOperationLogger Logger { get; private set; }
        protected OperationParameters OperationParameters { get; private set; }

        public static Operation CreateOperation(Type operationType)
        {
            Operation instance = (Operation)Activator.CreateInstance(operationType);
            return instance;
        }

        public static bool OperationTypeSupportsTarget(Type operationType, OperationTarget target)
        {
            return CreateOperation(operationType).SupportsTarget(target);
        }

        public async Task<OperationResult> Execute(OperationParameters operationParameters, IOperationLogger logger)
        {
            try
            {
                logger.Log($"Running operation '{OperationName}'");
                CheckRequirementsSatisfied(operationParameters);

                Logger = logger;
                OperationParameters = operationParameters;

                OperationResult result = await OnExecuted();

                Logger.Log($"Operation '{OperationName}' {(Terminated ? "terminated" : "completed")}", Terminated ? LogVerbosity.Warning : LogVerbosity.Log);
                return result;
            }
            catch (Exception e)
            {
                throw new Exception($"Exception encountered running operation '{OperationName}'", e);
            }
        }

        protected abstract Task<OperationResult> OnExecuted();

        public IEnumerable<Command> GetCommands(OperationParameters operationParameters)
        {
            if (!RequirementsSatisfied(operationParameters))
            {
                return new List<Command>();
            }

            return BuildCommands(operationParameters);
        }

        public string GetOutputPath(OperationParameters operationParameters)
        {
            if (operationParameters.OutputPathOverride != null)
            {
                return operationParameters.OutputPathOverride;
            }

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

        public HashSet<Type> GetRequiredOptionSetTypes(OperationTarget target)
        {
            if (target == null)
            {
                return null;
            }
            HashSet<Type> result = new();
            OperationParameters dummyParams = new();
            dummyParams.Target = target;

            if (GetRelevantEngineInstall(dummyParams) == null)
            {
                return null;
            }

            foreach (Command command in BuildCommands(dummyParams))
            {
                foreach (OperationOptions options in dummyParams.OptionsInstances)
                {
                    result.Add(options.GetType());
                }
            }
            return result;
        }

        public virtual bool SupportsConfiguration(BuildConfiguration configuration)
        {
            return true;
        }

        public virtual void CheckRequirementsSatisfied(OperationParameters operationParameters)
        {
            if (GetRelevantEngineInstall(operationParameters) == null)
            {
                throw new Exception("Engine install not found");
            }

            if (operationParameters.Target == null)
            {
                throw new Exception("Target not specified");
            }

            if (!SupportsTarget(operationParameters.Target))
            {
                throw new Exception($"Target {operationParameters.Target.GetName()} of type {operationParameters.Target.GetType()} is not supported");
            }

            BuildConfigurationOptions options = operationParameters.FindOptions<BuildConfigurationOptions>();

            if (options != null)
            {
                if (!SupportsConfiguration(options.Configuration))
                {
                    throw new Exception("Configuration is not supported");
                }

                if (!GetRelevantEngineInstall(operationParameters).SupportsConfiguration(options.Configuration))
                {
                    throw new Exception("Engine install does not support configuration");
                }
            }
        }

        public bool RequirementsSatisfied(OperationParameters operationParameters)
        {
            try
            {
                CheckRequirementsSatisfied(operationParameters);
                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        }

        public virtual bool ShouldReadOutputFromLogFile()
        {
            return false;
        }

        public abstract bool SupportsTarget(OperationTarget Target);

        public OperationTarget GetTarget(OperationParameters operationParameters)
        {
            return operationParameters.Target;
        }

        public virtual string GetLogsPath(OperationParameters operationParameters)
        {
            return null;
        }

        protected abstract IEnumerable<Command> BuildCommands(OperationParameters operationParameters);

        protected string GetTargetName(OperationParameters operationParameters)
        {
            return operationParameters.Target.GetName();
        }

        protected virtual string GetOperationName()
        {
            string name = GetType().Name;
            return name.SplitWordsByUppercase();
        }

        public void Terminate()
        {
            Terminated = true;
            OnTerminated();
        }

        protected virtual void OnTerminated()
        {

        }

    }

    public abstract class Operation<T> : Operation where T : OperationTarget
    {
        public new static Operation<T> CreateOperation(Type operationType)
        {
            Operation<T> instance = (Operation<T>)Activator.CreateInstance(operationType);
            return instance;
        }

        public override bool SupportsTarget(OperationTarget Target)
        {
            return Target is T;
        }

        public new T GetTarget(OperationParameters operationParameters)
        {
            return operationParameters.Target as T;
        }
    }
}
