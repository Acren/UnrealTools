using System;
using System.Collections.Generic;
using System.IO;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public abstract class Operation
    {
        public string OperationName => GetOperationName();
        public event OperationEndedEventHandler Ended;

        protected bool _terminated = false;

        public static Operation CreateOperation(Type operationType)
        {
            Operation instance = (Operation)Activator.CreateInstance(operationType);
            return instance;
        }

        public static bool OperationTypeSupportsTarget(Type operationType, OperationTarget target)
        {
            return CreateOperation(operationType).SupportsTarget(target);
        }

        public void Execute(OperationParameters operationParameters, IOperationLogger logger)
        {
            if (!RequirementsSatisfied(operationParameters))
            {
                throw new Exception("Requirements not satisfied");
            }

            OnExecuted(operationParameters, logger);
        }

        protected abstract void OnExecuted(OperationParameters operationParameters, IOperationLogger logger);

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

        public virtual bool RequirementsSatisfied(OperationParameters operationParameters)
        {
            if (GetRelevantEngineInstall(operationParameters) == null)
            {
                return false;
            }

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
            _terminated = true;
            OnTerminated();
        }

        protected virtual void OnTerminated()
        {

        }

        protected virtual void End(OperationResult result)
        {
            Ended?.Invoke(result);
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
