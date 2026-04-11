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
    /// Describes one complete project BuildCookRun invocation in terms of enabled phases and the explicit command
    /// settings that should shape the generated UAT arguments.
    /// </summary>
    public readonly struct BuildCookRunProjectRequest
    {
        public BuildCookRunProjectRequest(
            BuildCookRunProjectPhases phases,
            BuildConfiguration configuration = BuildConfiguration.Development,
            bool noDebugInfo = false,
            string? archiveDirectory = null,
            string? unrealExePath = null,
            string? additionalCookerOptions = null)
        {
            Phases = phases;
            Configuration = configuration;
            NoDebugInfo = noDebugInfo;
            ArchiveDirectory = archiveDirectory;
            UnrealExePath = unrealExePath;
            AdditionalCookerOptions = additionalCookerOptions;
        }

        /// <summary>
        /// The explicit BuildCookRun phases to turn into UAT flags for this invocation.
        /// </summary>
        public BuildCookRunProjectPhases Phases { get; }

        /// <summary>
        /// The client and server configuration BuildCookRun should use for this invocation.
        /// </summary>
        public BuildConfiguration Configuration { get; }

        /// <summary>
        /// Controls whether BuildCookRun should omit debug symbols from the packaged output.
        /// </summary>
        public bool NoDebugInfo { get; }

        /// <summary>
        /// When non-empty, instructs BuildCookRun to emit its archive layout to this directory.
        /// </summary>
        public string? ArchiveDirectory { get; }

        /// <summary>
        /// Overrides the cooker executable path when one specific cooker configuration should drive the cook.
        /// </summary>
        public string? UnrealExePath { get; }

        /// <summary>
        /// Supplies extra raw cooker arguments for flows that need one targeted cooker behavior toggle.
        /// </summary>
        public string? AdditionalCookerOptions { get; }

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
        /// enabled phases and explicit command settings, not the UAT argument plumbing.
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

            string configuration = request.Configuration.ToString();
            arguments.SetKeyValue("clientconfig", configuration);
            arguments.SetKeyValue("serverconfig", configuration);

            if (request.NoDebugInfo)
            {
                arguments.SetFlag("NoDebugInfo");
            }

            if (!string.IsNullOrWhiteSpace(request.ArchiveDirectory))
            {
                arguments.SetFlag("archive");
                arguments.SetKeyPath("archivedirectory", request.ArchiveDirectory!);
            }

            if (!string.IsNullOrWhiteSpace(request.UnrealExePath))
            {
                arguments.SetKeyPath("unrealexe", request.UnrealExePath!);
            }

            if (!string.IsNullOrWhiteSpace(request.AdditionalCookerOptions))
            {
                arguments.SetKeyValue("additionalcookeroptions", request.AdditionalCookerOptions!);
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
        /// Configurable BuildCookRun invocations only need additional argument passthrough because the preconfigured
        /// request already carries the command settings that would otherwise come from operation-specific option sets.
        /// </summary>
        protected override IEnumerable<Type> GetDeclaredOptionSetTypes(IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(AdditionalArgumentsOptions)
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
