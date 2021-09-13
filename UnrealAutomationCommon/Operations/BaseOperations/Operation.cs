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

                int warnings = 0;
                int errors = 0;

                Logger = logger;
                Logger.Output += (output, verbosity) =>
                {
                    if (verbosity == LogVerbosity.Error)
                    {
                        errors++;
                    }
                    else if (verbosity == LogVerbosity.Warning)
                    {
                        warnings++;
                    }
                };

                string requirementsError = CheckRequirementsSatisfied(operationParameters);
                if (requirementsError != null)
                {
                    Logger.Log(requirementsError, LogVerbosity.Error);
                    return new OperationResult(false);
                }

                OperationParameters = operationParameters;

                OperationResult result = await OnExecuted();

                if (Terminated)
                {
                    Logger.Log($"Operation '{OperationName}' terminated by user", LogVerbosity.Warning);
                }
                else
                {
                    Logger.Log($"Operation '{OperationName}' completed - {errors} error(s), {warnings} warning(s)");
                }

                if (result.Success)
                {
                    if (errors > 0)
                    {
                        Logger.Log($"{errors} error(s) encountered", LogVerbosity.Error);
                        result.Success = false;
                    }

                    if (warnings > 0)
                    {
                        Logger.Log($"{warnings} warning(s) encountered", LogVerbosity.Warning);
                        if (FailOnWarning())
                        {
                            Logger.Log("Operation fails on warnings", LogVerbosity.Error);
                            result.Success = false;
                        }
                    }
                }

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

        public string GetOperationTempPath()
        {
            return Path.Combine(OutputPaths.Root(), "Temp");
        }

        public string GetOutputPath(OperationParameters operationParameters)
        {
            if (operationParameters.OutputPathOverride != null)
            {
                return operationParameters.OutputPathOverride;
            }

            string path = Path.Combine(operationParameters.Target.OutputDirectory, OperationName.Replace(" ", ""));
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
                return new();
            }
            HashSet<Type> result = new();
            OperationParameters dummyParams = new();
            dummyParams.Target = target;

            if (!RequirementsSatisfied(dummyParams))
            {
                return new();
            }

            BuildCommands(dummyParams);

            foreach (OperationOptions options in dummyParams.OptionsInstances)
            {
                result.Add(options.GetType());
            }

            return result;
        }

        protected virtual bool FailOnWarning()
        {
            return false;
        }

        public virtual bool SupportsConfiguration(BuildConfiguration configuration)
        {
            return true;
        }

        public virtual string CheckRequirementsSatisfied(OperationParameters operationParameters)
        {
            if (operationParameters.Target == null)
            {
                return "Target not specified";
            }

            if (!SupportsTarget(operationParameters.Target))
            {
                return $"Target {operationParameters.Target.Name} of type {operationParameters.Target.GetType()} is not supported";
            }

            if (!operationParameters.Target.IsValid)
            {
                return $"Target {operationParameters.Target.Name} of type {operationParameters.Target.GetType()} is not valid";
            }

            if (TargetProvidesEngineInstall(operationParameters) && GetRelevantEngineInstall(operationParameters) == null)
            {
                return "Engine install not found";
            }

            BuildConfigurationOptions options = operationParameters.FindOptions<BuildConfigurationOptions>();

            if (options != null)
            {
                if (!SupportsConfiguration(options.Configuration))
                {
                    return "Configuration is not supported";
                }

                if (TargetProvidesEngineInstall(operationParameters) && !GetRelevantEngineInstall(operationParameters).SupportsConfiguration(options.Configuration))
                {
                    return "Engine install does not support configuration";
                }
            }

            return null;
        }

        public bool RequirementsSatisfied(OperationParameters operationParameters)
        {
            return CheckRequirementsSatisfied(operationParameters) == null;
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
