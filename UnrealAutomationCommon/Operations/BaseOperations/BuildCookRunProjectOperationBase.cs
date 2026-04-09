using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    /// <summary>
    /// Identifies the BuildCookRun phases a project operation should enable for one UAT invocation.
    /// </summary>
    [Flags]
    public enum BuildCookRunProjectPhases
    {
        None = 0,
        Build = 1 << 0,
        Cook = 1 << 1,
        Stage = 1 << 2,
        Pak = 1 << 3,
        Package = 1 << 4,
    }

    /// <summary>
    /// Describes one project BuildCookRun invocation in terms of enabled phases and the option groups that should still
    /// influence the generated command.
    /// </summary>
    public readonly struct BuildCookRunProjectRequest
    {
        public BuildCookRunProjectRequest(BuildCookRunProjectPhases phases, bool useArchiveOptions = false, bool useCookOptions = false)
        {
            Phases = phases;
            UseArchiveOptions = useArchiveOptions;
            UseCookOptions = useCookOptions;
        }

        /// <summary>
        /// The explicit BuildCookRun phases to turn into UAT flags for this invocation.
        /// </summary>
        public BuildCookRunProjectPhases Phases { get; }

        /// <summary>
        /// Controls whether package archive settings should add BuildCookRun archive arguments.
        /// </summary>
        public bool UseArchiveOptions { get; }

        /// <summary>
        /// Controls whether cook-specific options such as cooker configuration and wait-for-attach should be applied.
        /// </summary>
        public bool UseCookOptions { get; }

        /// <summary>
        /// Returns whether this invocation still enters the compile phase and therefore needs the shared Unreal build lock.
        /// </summary>
        public bool RequiresBuildLock => HasPhase(BuildCookRunProjectPhases.Build);

        /// <summary>
        /// Returns whether one specific BuildCookRun phase is enabled for the request.
        /// </summary>
        public bool HasPhase(BuildCookRunProjectPhases phase)
        {
            return (Phases & phase) == phase;
        }
    }

    /// <summary>
    /// Centralizes project-oriented BuildCookRun command assembly so concrete operations can differ only in which phases
    /// they run and which option groups they expose.
    /// </summary>
    public abstract class BuildCookRunProjectOperationBase : CommandProcessOperation<Project>
    {
        /// <summary>
        /// Returns the phase request that defines one concrete BuildCookRun invocation for the current parameter state.
        /// </summary>
        protected abstract BuildCookRunProjectRequest GetBuildCookRunRequest(ValidatedOperationParameters operationParameters);

        /// <summary>
        /// BuildCookRun only needs the shared Unreal build lock when the selected phase set still compiles binaries.
        /// </summary>
        protected override IEnumerable<ExecutionLock> GetExecutionLocks(ValidatedOperationParameters operationParameters)
        {
            foreach (ExecutionLock executionLock in base.GetExecutionLocks(operationParameters))
            {
                yield return executionLock;
            }

            /* Every BuildCookRun invocation launches RunUAT, and AutomationTool itself only allows one active instance per
               engine install. Serialize those calls per resolved engine so package-only flows can still run in parallel
               across different engine installs. */
            yield return UnrealExecutionLocks.GetAutomationToolLock(GetRequiredTargetEngineInstall(operationParameters));

            if (GetBuildCookRunRequest(operationParameters).RequiresBuildLock)
            {
                yield return UnrealExecutionLocks.GlobalBuild;
            }
        }

        /// <summary>
        /// Builds one BuildCookRun command from the shared request model so concrete operations only need to describe the
        /// enabled phases and option usage, not the UAT argument plumbing.
        /// </summary>
        protected override Command BuildCommand(ValidatedOperationParameters operationParameters)
        {
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            Project project = GetRequiredTarget(operationParameters);
            BuildCookRunProjectRequest request = GetBuildCookRunRequest(operationParameters);
            Arguments arguments = new();

            arguments.SetArgument("BuildCookRun");
            arguments.SetKeyPath("project", project.UProjectPath);
            ApplyPhaseArguments(arguments, request);

            string configuration = operationParameters.GetOptions<BuildConfigurationOptions>().Configuration.ToString();
            arguments.SetKeyValue("clientconfig", configuration);
            arguments.SetKeyValue("serverconfig", configuration);

            PackageOptions packageOptions = operationParameters.GetOptions<PackageOptions>();
            if (packageOptions.NoDebugInfo)
            {
                arguments.SetFlag("NoDebugInfo");
            }

            if (request.UseArchiveOptions && packageOptions.Archive)
            {
                arguments.SetFlag("archive");
                arguments.SetKeyPath("archivedirectory", GetOutputPath(operationParameters));
            }

            if (request.UseCookOptions)
            {
                ApplyCookOptions(arguments, operationParameters, engine);
            }

            arguments.ApplyCommonUATArguments(engine);
            arguments.AddAdditionalArguments(operationParameters);
            return new Command(engine.GetRunUATPath(), arguments.ToString());
        }

        /// <summary>
        /// Applies one flag per enabled BuildCookRun phase so the request shape stays readable and reusable.
        /// </summary>
        private static void ApplyPhaseArguments(Arguments arguments, BuildCookRunProjectRequest request)
        {
            if (request.HasPhase(BuildCookRunProjectPhases.Build))
            {
                arguments.SetFlag("build");
            }

            if (request.HasPhase(BuildCookRunProjectPhases.Cook))
            {
                arguments.SetFlag("cook");
            }

            if (request.HasPhase(BuildCookRunProjectPhases.Stage))
            {
                arguments.SetFlag("stage");
            }

            if (request.HasPhase(BuildCookRunProjectPhases.Pak))
            {
                arguments.SetFlag("pak");
            }

            if (request.HasPhase(BuildCookRunProjectPhases.Package))
            {
                arguments.SetFlag("package");
            }
        }

        /// <summary>
        /// Applies cooker-specific overrides only for requests that actually enter cook-driven packaging phases.
        /// </summary>
        private static void ApplyCookOptions(Arguments arguments, ValidatedOperationParameters operationParameters, Engine engine)
        {
            BuildConfiguration cookerConfiguration = operationParameters.GetOptions<CookOptions>().CookerConfiguration;
            if (cookerConfiguration != BuildConfiguration.Development)
            {
                string unrealExe = engine.GetEditorCmdExe(cookerConfiguration);
                arguments.SetKeyPath("unrealexe", unrealExe);
            }

            if (operationParameters.GetOptions<CookOptions>().WaitForAttach)
            {
                arguments.SetKeyValue("additionalcookeroptions", "-waitforattach");
            }
        }
    }

    /// <summary>
    /// Lets higher-level workflows such as Deploy Plugin compose custom BuildCookRun invocations without expanding the
    /// public operation catalog for every transient phase preset.
    /// </summary>
    internal sealed class ConfiguredBuildCookRunProjectOperation : BuildCookRunProjectOperationBase
    {
        private readonly string _operationName;
        private readonly BuildCookRunProjectRequest _request;

        public ConfiguredBuildCookRunProjectOperation(string operationName, BuildCookRunProjectRequest request)
        {
            _operationName = string.IsNullOrWhiteSpace(operationName)
                ? throw new ArgumentException("Operation name is required.", nameof(operationName))
                : operationName;
            _request = request;
        }

        /// <summary>
        /// Configurable BuildCookRun invocations expose the full set of project BuildCookRun option groups because deploy
        /// flows may reuse the same operation shape for build-only and package-only child calls.
        /// </summary>
        protected override IEnumerable<Type> GetDeclaredOptionSetTypes(IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(AdditionalArgumentsOptions),
                    typeof(BuildConfigurationOptions),
                    typeof(PackageOptions),
                    typeof(CookOptions)
                });
        }

        /// <summary>
        /// Returns the preconfigured BuildCookRun request captured when this transient operation instance was created.
        /// </summary>
        protected override BuildCookRunProjectRequest GetBuildCookRunRequest(ValidatedOperationParameters operationParameters)
        {
            return _request;
        }

        /// <summary>
        /// Preserves the caller-provided display name so logs and child-operation failures describe the specific phase
        /// preset Deploy Plugin asked for.
        /// </summary>
        protected override string GetOperationName()
        {
            return _operationName;
        }
    }
}
