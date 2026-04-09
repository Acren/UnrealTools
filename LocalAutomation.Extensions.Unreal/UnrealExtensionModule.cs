using System;
using System.Collections.Generic;
using System.Reflection;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationTypes;
using UnrealAutomationCommon.Unreal;
using RuntimeTarget = global::LocalAutomation.Runtime.IOperationTarget;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Registers the current Unreal-focused targets and operations as a compile-time LocalAutomation extension module.
/// </summary>
public sealed class UnrealExtensionModule : IExtensionModule
{
    /// <summary>
    /// Gets the stable identifier for the Unreal extension.
    /// </summary>
    public string Id => "unreal";

    /// <summary>
    /// Gets the display name used for diagnostics and future UI surfaces.
    /// </summary>
    public string DisplayName => "Unreal Engine";

    /// <summary>
    /// Unreal keeps its discoverable targets and operations in UnrealAutomationCommon, not beside the module type, so the
    /// host must scan that assembly for attributed descriptors during module registration.
    /// </summary>
    public IEnumerable<Assembly> GetDescriptorAssemblies()
    {
        return new[] { typeof(Project).Assembly };
    }

    /// <summary>
    /// Registers Unreal target descriptors, operations, and target creation logic.
    /// </summary>
    public void Register(IExtensionRegistry registry)
    {
        if (registry == null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        RegisterLegacyLoggerBridge();
        registry.RegisterTargetFactory(new UnrealPathTargetFactory());
        registry.RegisterOptionValueConverter(new EngineVersionListOptionValueConverter());
        registry.RegisterOptionValueConverter(new TraceChannelListOptionValueConverter());
        RegisterContextActions(registry);
    }

    /// <summary>
    /// Bridges the legacy UnrealAutomationCommon logger singleton onto the shared LocalAutomation application logger so
    /// old Unreal runtime code can log safely inside the new host.
    /// </summary>
    private static void RegisterLegacyLoggerBridge()
    {
        UnrealAutomationCommon.AppLogger.Instance.Logger = ApplicationLogger.Logger;
    }

    /// <summary>
    /// Registers the first parity-focused set of target actions used by the Avalonia shell.
    /// </summary>
    private static void RegisterContextActions(IExtensionRegistry registry)
    {
        registry.RegisterContextAction(new ContextActionDescriptor(
            id: new ContextActionId("unreal.target.open-directory"),
            displayName: "Open Directory",
            targetType: typeof(RuntimeTarget),
            execute: target => RunProcess.OpenDirectory(((RuntimeTarget)target).TargetDirectory)));

        registry.RegisterContextAction(new ContextActionDescriptor(
            id: new ContextActionId("unreal.target.open-output"),
            displayName: "Open Output",
            targetType: typeof(RuntimeTarget),
            execute: target => RunProcess.OpenDirectory(((RuntimeTarget)target).OutputDirectory)));

        registry.RegisterContextAction(new ContextActionDescriptor(
            id: new ContextActionId("unreal.project.open-staged-build"),
            displayName: "Open Staged Build",
            targetType: typeof(Project),
            execute: target =>
            {
                Project project = (Project)target;
                Engine? engine = project.EngineInstance;
                if (engine == null)
                {
                    throw new InvalidOperationException($"Project '{project.DisplayName}' does not currently resolve to an engine install.");
                }

                RunProcess.OpenDirectory(project.GetStagedBuildWindowsPath(engine));
            },
            canExecute: target => ((Project)target).EngineInstance != null));
    }
}
