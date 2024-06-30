using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    public abstract class Operation
    {
        public string OperationName => GetOperationName();
        public bool SupportsMultipleEngines => false;

        public bool Executing { get; private set; }
        public bool Cancelled { get; private set; }
        protected ILogger Logger { get; private set; }
        protected OperationParameters OperationParameters { get; private set; }

        public static Operation CreateOperation(Type operationType)
        {
            if (operationType == null)
            {
                throw new ArgumentNullException(nameof(operationType));
            }

            Operation instance = (Operation)Activator.CreateInstance(operationType);
            return instance;
        }

        public static bool OperationTypeSupportsTarget(Type operationType, IOperationTarget target)
        {
            if (operationType == null)
            {
                return false;
            }

            return CreateOperation(operationType).SupportsTarget(target);
        }

        public async Task<OperationResult> ExecuteOnThread(OperationParameters operationParameters, ILogger logger, CancellationToken token)
        {
            TaskCompletionSource<OperationResult> tcs = new();
            ThreadPool.QueueUserWorkItem( async (object state) =>
            {
                OperationResult result = await Execute(operationParameters, logger, token);
                tcs.SetResult(result);
            });
            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task<OperationResult> Execute(OperationParameters operationParameters, ILogger logger, CancellationToken token)
        {
            try
            {
                logger.LogSectionHeader($"Running operation '{OperationName}'");

                var warnings = 0;
                var errors = 0;

                EventLogger eventLogger = new();
                Logger = eventLogger;
                eventLogger.Output += (level, output) =>
                {
                    if (level >= LogLevel.Error)
                    {
                        errors++;
                    }
                    else if (level == LogLevel.Warning)
                    {
                        warnings++;
                    }
                    logger.Log(level, output);
                };

                string requirementsError = CheckRequirementsSatisfied(operationParameters);
                if (requirementsError != null)
                {
                    Logger.LogError(requirementsError);
                    return new OperationResult(false);
                }

                OperationParameters = operationParameters;

                Executing = true;
                var mainTask = OnExecuted(token);
                Executing = false;

                OperationResult result = await mainTask;

                if (Cancelled)
                {
                    Logger.LogWarning($"Operation '{OperationName}' terminated by user");
                }
                else
                {
                    Logger.LogInformation($"Operation '{OperationName}' completed - {errors} error(s), {warnings} warning(s)");
                }

                if (result.Success)
                {
                    if (errors > 0)
                    {
                        Logger.LogError($"{errors} error(s) encountered");
                        result.Success = false;
                    }

                    if (warnings > 0)
                    {
                        Logger.LogWarning($"{warnings} warning(s) encountered");
                        if (FailOnWarning())
                        {
                            Logger.LogError("Operation fails on warnings");
                            result.Success = false;
                        }
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                Executing = false;
                throw new Exception($"Exception encountered running operation '{OperationName}'", e);
            }
        }

        protected abstract Task<OperationResult> OnExecuted(CancellationToken token);

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
            return GetTarget(operationParameters) is IEngineInstanceProvider;
        }

        public Engine GetTargetEngineInstall(OperationParameters operationParameters)
        {
            return operationParameters.Engine;
        }

        public HashSet<Type> GetRequiredOptionSetTypes(IOperationTarget target)
        {
            if (target == null)
            {
                return new HashSet<Type>();
            }

            HashSet<Type> result = new();
            OperationParameters dummyParams = new();
            dummyParams.Target = target;

            if (!RequirementsSatisfied(dummyParams))
            {
                return new HashSet<Type>();
            }

            BuildCommands(dummyParams);

            foreach (OperationOptions options in dummyParams.OptionsInstances) result.Add(options.GetType());

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

            if (TargetProvidesEngineInstall(operationParameters) && GetTargetEngineInstall(operationParameters) == null)
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

                if (TargetProvidesEngineInstall(operationParameters) && !GetTargetEngineInstall(operationParameters).SupportsConfiguration(options.Configuration))
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

        protected void SetCancelled()
        {
            Cancelled = true;
        }
    }

    public abstract class Operation<T> : Operation where T : IOperationTarget
    {
        public new static Operation<T> CreateOperation(Type operationType)
        {
            var instance = (Operation<T>)Activator.CreateInstance(operationType);
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