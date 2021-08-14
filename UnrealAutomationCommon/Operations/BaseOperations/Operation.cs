using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.BaseOperations
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

        public static bool OperationTypeSupportsTarget(Type operationType, IOperationTarget target)
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

            string path = Path.Combine(operationParameters.Target.OutputPath, OperationName.Replace(" ", ""));
            return path;
        }

        public bool TargetProvidesEngineInstall(OperationParameters operationParameters)
        {
            return GetTarget(operationParameters) is IEngineInstallProvider;
        }

        public EngineInstall GetRelevantEngineInstall(OperationParameters operationParameters)
        {
            if (GetTarget(operationParameters) is not IEngineInstallProvider)
            {
                return null;
            }

            IEngineInstallProvider engineInstallProvider = GetTarget(operationParameters) as IEngineInstallProvider;
            return engineInstallProvider?.EngineInstall;
        }

        public HashSet<Type> GetRequiredOptionSetTypes(IOperationTarget target)
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

            BuildCommands(dummyParams);

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

        public virtual void CheckRequirementsSatisfied(OperationParameters operationParameters)
        {
            if (operationParameters.Target == null)
            {
                throw new Exception("Target not specified");
            }

            if (!SupportsTarget(operationParameters.Target))
            {
                throw new Exception($"Target {operationParameters.Target.Name} of type {operationParameters.Target.GetType()} is not supported");
            }

            if (TargetProvidesEngineInstall(operationParameters) && GetRelevantEngineInstall(operationParameters) == null)
            {
                throw new Exception("Engine install not found");
            }

            BuildConfigurationOptions options = operationParameters.FindOptions<BuildConfigurationOptions>();

            if (options != null)
            {
                if (!SupportsConfiguration(options.Configuration))
                {
                    throw new Exception("Configuration is not supported");
                }

                if (TargetProvidesEngineInstall(operationParameters) && !GetRelevantEngineInstall(operationParameters).SupportsConfiguration(options.Configuration))
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
            catch (Exception)
            {
                return false;
            }
        }

        public virtual bool ShouldReadOutputFromLogFile()
        {
            return false;
        }

        public abstract bool SupportsTarget(IOperationTarget Target);

        public IOperationTarget GetTarget(OperationParameters operationParameters)
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
            return operationParameters.Target.Name;
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

    public abstract class Operation<T> : Operation where T : IOperationTarget
    {
        public new static Operation<T> CreateOperation(Type operationType)
        {
            Operation<T> instance = (Operation<T>)Activator.CreateInstance(operationType);
            return instance;
        }

        public override bool SupportsTarget(IOperationTarget Target)
        {
            return Target is T;
        }

        public new T GetTarget(OperationParameters operationParameters)
        {
            return (T)operationParameters.Target;
        }
    }
}
