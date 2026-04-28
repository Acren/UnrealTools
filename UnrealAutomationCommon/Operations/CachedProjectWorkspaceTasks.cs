using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core.IO;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Authors the repeated cached-project workspace lifecycle while leaving build semantics to ordinary operations such
    /// as BuildEditorTarget, BuildProjectTarget, and BuildPlugin.
    /// </summary>
    internal static class CachedProjectWorkspaceTasks
    {
        /// <summary>
        /// Creates a visible cached-workspace parent task with refresh, build, and copy-back child tasks beneath it.
        /// </summary>
        public static ExecutionTaskBuilder AddBuild(
            ExecutionTaskScopeBuilder scope,
            ValidatedOperationParameters operationParameters,
            string title,
            ExecutionLock cacheLock,
            string operationName,
            string role,
            string subjectName,
            Func<IOperationParameterContext, Engine> getEngine,
            Func<IOperationParameterContext, string> getSourceProjectPath,
            Operation buildOperation,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters)
        {
            _ = scope ?? throw new ArgumentNullException(nameof(scope));
            _ = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
            string resolvedTitle = string.IsNullOrWhiteSpace(title)
                ? throw new ArgumentException("A cached workspace task title is required.", nameof(title))
                : title;
            _ = cacheLock ?? throw new ArgumentNullException(nameof(cacheLock));
            string resolvedOperationName = string.IsNullOrWhiteSpace(operationName)
                ? throw new ArgumentException("Operation name is required for a cached workspace build.", nameof(operationName))
                : operationName;
            string resolvedRole = string.IsNullOrWhiteSpace(role)
                ? throw new ArgumentException("Role is required for a cached workspace build.", nameof(role))
                : role;
            string resolvedSubjectName = string.IsNullOrWhiteSpace(subjectName)
                ? throw new ArgumentException("Subject name is required for a cached workspace build.", nameof(subjectName))
                : subjectName;
            _ = getEngine ?? throw new ArgumentNullException(nameof(getEngine));
            _ = getSourceProjectPath ?? throw new ArgumentNullException(nameof(getSourceProjectPath));
            Operation resolvedBuildOperation = buildOperation ?? throw new ArgumentNullException(nameof(buildOperation));
            _ = createBuildParameters ?? throw new ArgumentNullException(nameof(createBuildParameters));

            ExecutionTaskBuilder parent = scope.Task(resolvedTitle)
                .WithExecutionLocks(cacheLock);
            parent.Children(steps =>
            {
                steps.Task("Refresh Cached Workspace")
                    .Describe("Refresh authored project inputs into the stable cached build workspace while preserving reusable intermediates")
                    .Run(context => RefreshCachedWorkspaceAsync(context, resolvedOperationName, resolvedRole, resolvedSubjectName, getEngine, getSourceProjectPath, createBuildParameters));

                steps.Task("Build Cached Target")
                    .Describe("Run the direct Unreal build against the refreshed cached project workspace")
                    .AddChildOperation(
                        resolvedBuildOperation,
                        () => CreateBuildAuthoringParameters(operationParameters, resolvedBuildOperation),
                        context => CreateCachedBuildParameters(context, resolvedOperationName, resolvedRole, resolvedSubjectName, getEngine, getSourceProjectPath, resolvedBuildOperation, createBuildParameters))
                    .HideInGraph();

                steps.Task("Copy Cached Build Outputs")
                    .Describe("Merge generated cached build outputs back into the session project used by later steps")
                    .Run(context => CopyCachedBuildOutputsAsync(context, resolvedOperationName, resolvedRole, resolvedSubjectName, getEngine, getSourceProjectPath, createBuildParameters));
            });

            return parent;
        }

        /// <summary>
        /// Creates the authoring-time parameter bag required to expand the real child build operation's static task shape.
        /// The concrete cached workspace target is selected later at runtime, so this uses the nearest existing compatible
        /// target only to satisfy the child operation's target and option declarations during plan construction.
        /// </summary>
        private static OperationParameters CreateBuildAuthoringParameters(ValidatedOperationParameters operationParameters, Operation buildOperation)
        {
            IOperationTarget planTarget = GetPlanBuildTarget(operationParameters.Target, buildOperation);
            OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = planTarget;
            return parameters;
        }

        /// <summary>
        /// Walks from the parent operation target toward its root until it finds the target type supported by the child
        /// build operation. This keeps project-build operations authored against a host project and plugin-build operations
        /// authored against the plugin without call sites knowing about placeholder targets.
        /// </summary>
        private static IOperationTarget GetPlanBuildTarget(IOperationTarget sourceTarget, Operation buildOperation)
        {
            for (IOperationTarget? currentTarget = sourceTarget; currentTarget != null; currentTarget = currentTarget.ParentTarget)
            {
                if (buildOperation.SupportsTarget(currentTarget))
                {
                    return currentTarget;
                }
            }

            throw new InvalidOperationException($"Cached build operation '{buildOperation.OperationName}' does not support target '{sourceTarget.TypeName}' or any parent target.");
        }

        /// <summary>
        /// Refreshes authored source-project inputs into the stable cached workspace before any Build.bat command runs.
        /// </summary>
        private static async Task RefreshCachedWorkspaceAsync(
            ExecutionTaskContext context,
            string operationName,
            string role,
            string subjectName,
            Func<IOperationParameterContext, Engine> getEngine,
            Func<IOperationParameterContext, string> getSourceProjectPath,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters)
        {
            using CachedProjectWorkspaceRequest request = CreateRequest(context, operationName, role, subjectName, getEngine, getSourceProjectPath, createBuildParameters);
            using Project cachedProject = UnrealBuildWorkspaceCache.PrepareProjectWorkspace(
                request.SourceProject,
                request.CachedProjectPath,
                request.MaterializationSpec,
                context.Logger,
                context.CancellationToken);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates the runtime build parameters for the imported build operation after opening the cached project prepared
        /// by the refresh child task.
        /// </summary>
        private static OperationParameters CreateCachedBuildParameters(
            IOperationParameterContext context,
            string operationName,
            string role,
            string subjectName,
            Func<IOperationParameterContext, Engine> getEngine,
            Func<IOperationParameterContext, string> getSourceProjectPath,
            Operation buildOperation,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters)
        {
            using CachedProjectWorkspaceRequest request = CreateRequest(context, operationName, role, subjectName, getEngine, getSourceProjectPath, createBuildParameters);
            Project cachedProject = CreateRequiredProject(request.CachedProjectPath, $"Cached build workspace is not available for role '{role}'");
            OperationParameters buildParameters = createBuildParameters(context, cachedProject)
                ?? throw new InvalidOperationException($"Cached build operation '{buildOperation.OperationName}' did not create operation parameters.");

            try
            {
                if (buildParameters.Target == null)
                {
                    throw new InvalidOperationException($"Cached build operation '{buildOperation.OperationName}' did not select a target.");
                }

                if (!buildOperation.SupportsTarget(buildParameters.Target))
                {
                    throw new InvalidOperationException($"Cached build operation '{buildOperation.OperationName}' does not support runtime target '{buildParameters.Target.TypeName}'.");
                }

                if (!ReferenceEquals(buildParameters.Target, cachedProject))
                {
                    cachedProject.Dispose();
                }

                return buildParameters;
            }
            catch
            {
                cachedProject.Dispose();
                if (!ReferenceEquals(buildParameters.Target, cachedProject) && buildParameters.Target is IDisposable disposableTarget)
                {
                    disposableTarget.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// Copies generated binaries and receipts from the cached workspace back to the session project that downstream
        /// package, launch, and archive tasks consume.
        /// </summary>
        private static async Task CopyCachedBuildOutputsAsync(
            ExecutionTaskContext context,
            string operationName,
            string role,
            string subjectName,
            Func<IOperationParameterContext, Engine> getEngine,
            Func<IOperationParameterContext, string> getSourceProjectPath,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters)
        {
            using CachedProjectWorkspaceRequest request = CreateRequest(context, operationName, role, subjectName, getEngine, getSourceProjectPath, createBuildParameters);
            using Project cachedProject = CreateRequiredProject(request.CachedProjectPath, $"Cached build workspace is not available for role '{role}'");
            UnrealBuildWorkspaceCache.CopyProjectBuildOutputs(cachedProject, request.SourceProject, context.Logger, context.CancellationToken);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Opens the source project and derives cache identity from the same configuration and compiler options that the
        /// build operation will use for its command line.
        /// </summary>
        private static CachedProjectWorkspaceRequest CreateRequest(
            IOperationParameterContext context,
            string operationName,
            string role,
            string subjectName,
            Func<IOperationParameterContext, Engine> getEngine,
            Func<IOperationParameterContext, string> getSourceProjectPath,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters)
        {
            Engine engine = getEngine(context) ?? throw new InvalidOperationException($"Cached build role '{role}' did not resolve an Unreal engine.");
            Project sourceProject = CreateRequiredProject(getSourceProjectPath(context), $"Cached build source project is not available for role '{role}'");
            try
            {
                OperationParameters buildParameters = createBuildParameters(context, sourceProject)
                    ?? throw new InvalidOperationException($"Cached build role '{role}' did not create operation parameters.");
                IDisposable? disposableTarget = ReferenceEquals(buildParameters.Target, sourceProject)
                    ? null
                    : buildParameters.Target as IDisposable;
                try
                {
                    BuildConfiguration configuration = buildParameters.GetOptions<BuildConfigurationOptions>().Configuration;
                    UbtCompilerOptions compilerOptions = buildParameters.GetOptions<UbtCompilerOptions>();
                    IReadOnlySet<string> projectPluginNames = MaterializationSpecs.GetProjectPluginNames(sourceProject);
                    FileMaterializationSpec materializationSpec = MaterializationSpecs.CreateProject(sourceProject, projectPluginNames);
                    return new CachedProjectWorkspaceRequest(
                        engine,
                        sourceProject,
                        operationName,
                        role,
                        subjectName,
                        configuration,
                        compilerOptions.Compiler,
                        compilerOptions.CppStandard,
                        materializationSpec,
                        projectPluginNames.Select(pluginName => $"ProjectPlugin:{pluginName}"));
                }
                finally
                {
                    disposableTarget?.Dispose();
                }
            }
            catch
            {
                sourceProject.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens a project path and fails loudly when the descriptor is missing or invalid.
        /// </summary>
        private static Project CreateRequiredProject(string projectPath, string failureMessage)
        {
            Project project = new(projectPath);
            if (project.IsValid)
            {
                return project;
            }

            project.Dispose();
            throw new InvalidOperationException($"{failureMessage}: {projectPath}");
        }

    }

    /// <summary>
    /// Describes one runtime cached project workspace invocation and owns the source-project handle for that invocation.
    /// </summary>
    internal sealed class CachedProjectWorkspaceRequest : IDisposable
    {
        public CachedProjectWorkspaceRequest(
            Engine engine,
            Project sourceProject,
            string operationName,
            string role,
            string subjectName,
            BuildConfiguration configuration,
            UbtCompiler compiler,
            UbtCppStandard cppStandard,
            FileMaterializationSpec materializationSpec,
            IEnumerable<string> shapeParts)
        {
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            SourceProject = sourceProject ?? throw new ArgumentNullException(nameof(sourceProject));
            OperationName = string.IsNullOrWhiteSpace(operationName) ? throw new ArgumentException("Operation name is required for a cached workspace request.", nameof(operationName)) : operationName;
            Role = string.IsNullOrWhiteSpace(role) ? throw new ArgumentException("Role is required for a cached workspace request.", nameof(role)) : role;
            SubjectName = string.IsNullOrWhiteSpace(subjectName) ? throw new ArgumentException("Subject name is required for a cached workspace request.", nameof(subjectName)) : subjectName;
            Configuration = configuration;
            Compiler = compiler;
            CppStandard = cppStandard;
            MaterializationSpec = materializationSpec ?? throw new ArgumentNullException(nameof(materializationSpec));
            ShapeParts = (shapeParts ?? Array.Empty<string>()).ToList();
        }

        public Engine Engine { get; }

        public Project SourceProject { get; }

        public string OperationName { get; }

        public string Role { get; }

        public string SubjectName { get; }

        public BuildConfiguration Configuration { get; }

        public UbtCompiler Compiler { get; }

        public UbtCppStandard CppStandard { get; }

        public FileMaterializationSpec MaterializationSpec { get; }

        public IReadOnlyList<string> ShapeParts { get; }

        public string CacheKey => UnrealBuildWorkspaceCache.CreateProjectCacheKey(Engine, OperationName, Role, SubjectName, SourceProject.Name, Configuration, Compiler, CppStandard, ShapeParts);

        public string CachedProjectPath => UnrealBuildWorkspaceCache.GetProjectWorkspacePath(CacheKey);

        /// <summary>
        /// Closes source-project file watchers created only for this cache request.
        /// </summary>
        public void Dispose()
        {
            SourceProject.Dispose();
        }
    }
}
