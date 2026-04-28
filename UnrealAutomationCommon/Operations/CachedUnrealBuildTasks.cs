using System;
using System.Threading.Tasks;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Operations.OperationTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Authors Unreal-specific cached build tasks while keeping operation plans focused on their dependency graph.
    /// </summary>
    internal static class CachedUnrealBuildTasks
    {
        /// <summary>
        /// Keeps UAT's generated BuildPlugin host project available so package runs can reuse its intermediates.
        /// </summary>
        private const string PreserveHostProjectArgument = "-NoDeleteHostProject";

        /// <summary>
        /// Adds a cached direct project-build task that refreshes a source project, runs the supplied build operation, and
        /// copies the generated build outputs back to the source project.
        /// </summary>
        public static ExecutionTaskBuilder AddProjectBuild<TState>(
            ExecutionTaskScopeBuilder scope,
            string title,
            string operationName,
            string role,
            string subjectName,
            EngineVersion engineVersion,
            Operation buildOperation,
            ValidatedOperationParameters operationParameters,
            Func<TState, Engine> getEngine,
            Func<TState, string> getSourceProjectPath,
            BuildConfiguration configuration,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters,
            UbtCompiler compiler = UbtCompiler.Default,
            UbtCppStandard cppStandard = UbtCppStandard.Default)
            where TState : class
        {
            _ = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
            Operation resolvedBuildOperation = buildOperation ?? throw new ArgumentNullException(nameof(buildOperation));
            _ = getEngine ?? throw new ArgumentNullException(nameof(getEngine));
            _ = getSourceProjectPath ?? throw new ArgumentNullException(nameof(getSourceProjectPath));
            _ = createBuildParameters ?? throw new ArgumentNullException(nameof(createBuildParameters));
            UnrealBuildWorkspaceCache.ProjectBuildWorkspace? workspace = null;

            // The cached workspace is created during prepare and reused by the run and copy-back steps so cache identity
            // and materialization data are computed once for this authored cached operation.
            return AddCachedOperation(
                scope,
                title,
                UnrealExecutionLocks.GetBuildWorkspaceCacheLock(operationName, role, subjectName, engineVersion),
                resolvedBuildOperation,
                operationParameters,
                context =>
                {
                    TState state = context.GetData<TState>();
                    workspace = UnrealBuildWorkspaceCache.CreateProjectBuildWorkspace(
                        getEngine(state),
                        operationName,
                        role,
                        subjectName,
                        getSourceProjectPath(state),
                        configuration,
                        compiler,
                        cppStandard);
                    return workspace.PrepareAsync(context);
                },
                context => CreateProjectBuildParameters(context, GetPreparedWorkspace(workspace, title), resolvedBuildOperation, createBuildParameters),
                context => GetPreparedWorkspace(workspace, title).CopyOutputsAsync(context));
        }

        /// <summary>
        /// Adds a cached BuildPlugin package task that preserves UAT's host-project cache while copying only the packaged
        /// plugin payload back to the deployment workspace.
        /// </summary>
        public static ExecutionTaskBuilder AddPluginPackage<TState>(
            ExecutionTaskScopeBuilder scope,
            string title,
            string operationName,
            string role,
            string pluginName,
            EngineVersion engineVersion,
            ValidatedOperationParameters operationParameters,
            Func<TState, Engine> getEngine,
            Func<TState, string> getStagingPluginPath,
            Func<TState, string> getDestinationPluginPath)
            where TState : class
        {
            _ = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
            _ = getEngine ?? throw new ArgumentNullException(nameof(getEngine));
            _ = getStagingPluginPath ?? throw new ArgumentNullException(nameof(getStagingPluginPath));
            _ = getDestinationPluginPath ?? throw new ArgumentNullException(nameof(getDestinationPluginPath));
            PackagePlugin packageOperation = new();
            PluginBuildOptions pluginBuildOptions = operationParameters.GetOptions<PluginBuildOptions>();
            UnrealBuildWorkspaceCache.PluginPackageWorkspace? workspace = null;

            // PackagePlugin needs a fresh runtime parameter bag because UAT writes the package payload directly into the
            // prepared cache root.
            return AddCachedOperation(
                scope,
                title,
                UnrealExecutionLocks.GetBuildWorkspaceCacheLock(operationName, role, pluginName, engineVersion),
                packageOperation,
                operationParameters,
                context =>
                {
                    TState state = context.GetData<TState>();
                    workspace = UnrealBuildWorkspaceCache.CreatePluginPackageWorkspace(
                        getEngine(state),
                        operationName,
                        role,
                        getStagingPluginPath(state),
                        getDestinationPluginPath(state),
                        pluginBuildOptions);
                    return workspace.PrepareAsync(context);
                },
                context => CreatePluginPackageParameters(context, GetPreparedWorkspace(workspace, title), packageOperation, pluginBuildOptions),
                context => GetPreparedWorkspace(workspace, title).CopyOutputsAsync(context));
        }

        /// <summary>
        /// Creates a visible cached-operation parent with explicit prepare, wrapped operation, and copy-back children.
        /// </summary>
        private static ExecutionTaskBuilder AddCachedOperation(
            ExecutionTaskScopeBuilder scope,
            string title,
            ExecutionLock cacheLock,
            Operation cachedOperation,
            ValidatedOperationParameters operationParameters,
            Func<ExecutionTaskContext, Task> prepareAsync,
            Func<IOperationParameterContext, OperationParameters> createRuntimeParameters,
            Func<ExecutionTaskContext, Task> copyOutputsAsync)
        {
            _ = scope ?? throw new ArgumentNullException(nameof(scope));
            string resolvedTitle = string.IsNullOrWhiteSpace(title)
                ? throw new ArgumentException("A cached operation title is required.", nameof(title))
                : title;
            _ = cacheLock ?? throw new ArgumentNullException(nameof(cacheLock));
            Operation resolvedOperation = cachedOperation ?? throw new ArgumentNullException(nameof(cachedOperation));
            _ = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
            _ = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
            _ = createRuntimeParameters ?? throw new ArgumentNullException(nameof(createRuntimeParameters));
            _ = copyOutputsAsync ?? throw new ArgumentNullException(nameof(copyOutputsAsync));

            ExecutionTaskBuilder parent = scope.Task(resolvedTitle)
                .WithExecutionLocks(cacheLock);
            parent.Children(steps =>
            {
                steps.Task("Prepare Cached Workspace")
                    .Describe("Prepare cache inputs before the cached operation runs")
                    .Run(context => prepareAsync(context));

                steps.Task("Run Cached Operation")
                    .Describe($"Run {resolvedOperation.OperationName} against the prepared cache workspace")
                    .AddChildOperation(
                        resolvedOperation,
                        () => CreateAuthoringParameters(operationParameters, resolvedOperation),
                        context => CreateRuntimeParameters(context, resolvedOperation, createRuntimeParameters))
                    .HideInGraph();

                steps.Task("Copy Cached Outputs")
                    .Describe("Copy generated cache outputs back into the session workspace")
                    .Run(context => copyOutputsAsync(context));
            });

            return parent;
        }

        /// <summary>
        /// Creates authoring-time parameters by selecting the nearest plan target supported by the wrapped operation.
        /// </summary>
        private static OperationParameters CreateAuthoringParameters(ValidatedOperationParameters operationParameters, Operation cachedOperation)
        {
            IOperationTarget planTarget = GetPlanTarget(operationParameters.Target, cachedOperation);
            OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = planTarget;
            return parameters;
        }

        /// <summary>
        /// Walks the plan target hierarchy until it finds a target that the wrapped operation can declare against.
        /// </summary>
        private static IOperationTarget GetPlanTarget(IOperationTarget sourceTarget, Operation cachedOperation)
        {
            for (IOperationTarget? currentTarget = sourceTarget; currentTarget != null; currentTarget = currentTarget.ParentTarget)
            {
                if (cachedOperation.SupportsTarget(currentTarget))
                {
                    return currentTarget;
                }
            }

            throw new InvalidOperationException($"Cached operation '{cachedOperation.OperationName}' does not support target '{sourceTarget.TypeName}' or any parent target.");
        }

        /// <summary>
        /// Creates and validates runtime parameters for the supplied child operation before the scheduler starts it.
        /// </summary>
        private static OperationParameters CreateRuntimeParameters(
            IOperationParameterContext context,
            Operation cachedOperation,
            Func<IOperationParameterContext, OperationParameters> createRuntimeParameters)
        {
            OperationParameters parameters = createRuntimeParameters(context)
                ?? throw new InvalidOperationException($"Cached operation '{cachedOperation.OperationName}' did not create operation parameters.");
            ValidateRuntimeTarget(cachedOperation, parameters);
            return parameters;
        }

        /// <summary>
        /// Creates runtime parameters for a cached project build and owns the cached-project target lifetime on failures.
        /// </summary>
        private static OperationParameters CreateProjectBuildParameters(
            IOperationParameterContext context,
            UnrealBuildWorkspaceCache.ProjectBuildWorkspace workspace,
            Operation buildOperation,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _ = buildOperation ?? throw new ArgumentNullException(nameof(buildOperation));
            _ = createBuildParameters ?? throw new ArgumentNullException(nameof(createBuildParameters));
            Project cachedProject = workspace.OpenCachedProject();
            OperationParameters? createdParameters = null;
            try
            {
                OperationParameters buildParameters = createBuildParameters(context, cachedProject)
                    ?? throw new InvalidOperationException($"Cached build operation '{buildOperation.OperationName}' did not create operation parameters.");
                createdParameters = buildParameters;
                ValidateRuntimeTarget(buildOperation, buildParameters);
                if (!ReferenceEquals(buildParameters.Target, cachedProject))
                {
                    cachedProject.Dispose();
                }

                return buildParameters;
            }
            catch
            {
                cachedProject.Dispose();
                if (createdParameters != null && !ReferenceEquals(createdParameters.Target, cachedProject) && createdParameters.Target is IDisposable disposableTarget)
                {
                    disposableTarget.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// Creates PackagePlugin runtime parameters that direct UAT output into the prepared package cache root.
        /// </summary>
        private static OperationParameters CreatePluginPackageParameters(
            IOperationParameterContext context,
            UnrealBuildWorkspaceCache.PluginPackageWorkspace workspace,
            Operation packageOperation,
            PluginBuildOptions pluginBuildOptions)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _ = packageOperation ?? throw new ArgumentNullException(nameof(packageOperation));
            _ = pluginBuildOptions ?? throw new ArgumentNullException(nameof(pluginBuildOptions));
            Plugin stagingPlugin = workspace.OpenStagingPlugin();
            try
            {
                OperationParameters parameters = packageOperation.CreateParameters();
                parameters.Target = stagingPlugin;
                parameters.OutputPathOverride = workspace.CachePath;
                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { workspace.EngineVersion };
                parameters.SetOptions((PluginBuildOptions)pluginBuildOptions.Clone());
                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = PreserveHostProjectArgument;
                ValidateRuntimeTarget(packageOperation, parameters);
                return parameters;
            }
            catch
            {
                // Runtime parameters own the staging-plugin target only after successful creation.
                stagingPlugin.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Ensures a workspace prepared earlier in the cached task sequence is available to a later sibling step.
        /// </summary>
        private static TWorkspace GetPreparedWorkspace<TWorkspace>(TWorkspace? workspace, string taskTitle)
            where TWorkspace : class
        {
            return workspace ?? throw new InvalidOperationException($"Cached workspace for '{taskTitle}' was requested before the prepare step completed.");
        }

        /// <summary>
        /// Ensures a wrapped operation parameter factory selected a supported runtime target before returning it.
        /// </summary>
        private static void ValidateRuntimeTarget(Operation operation, OperationParameters parameters)
        {
            if (parameters.Target == null)
            {
                throw new InvalidOperationException($"Cached operation '{operation.OperationName}' did not select a target.");
            }

            if (!operation.SupportsTarget(parameters.Target))
            {
                throw new InvalidOperationException($"Cached operation '{operation.OperationName}' does not support runtime target '{parameters.Target.TypeName}'.");
            }
        }
    }
}
