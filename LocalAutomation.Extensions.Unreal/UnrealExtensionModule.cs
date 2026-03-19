using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationTypes;
using UnrealAutomationCommon.Unreal;

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
    /// Registers Unreal target descriptors, operations, and target creation logic.
    /// </summary>
    public void Register(IExtensionRegistry registry)
    {
        if (registry == null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        registry.RegisterTarget(new TargetDescriptor("unreal.project", "Project", typeof(Project)));
        registry.RegisterTarget(new TargetDescriptor("unreal.plugin", "Plugin", typeof(Plugin)));
        registry.RegisterTarget(new TargetDescriptor("unreal.engine", "Engine", typeof(Engine)));
        registry.RegisterTarget(new TargetDescriptor("unreal.package", "Package", typeof(Package)));
        registry.RegisterTargetFactory(new UnrealPathTargetFactory());
        registry.RegisterOperationAdapter(new UnrealOperationAdapter());
        registry.RegisterRunnerAdapter(new UnrealRunnerAdapter());
        RegisterContextActions(registry);

        foreach (OperationDescriptor descriptor in BuildOperationDescriptors())
        {
            registry.RegisterOperation(descriptor);
        }
    }

    /// <summary>
    /// Preserves the existing user-facing operation order from the WPF app while formalizing it into extension
    /// descriptors.
    /// </summary>
    private static IReadOnlyList<OperationDescriptor> BuildOperationDescriptors()
    {
        List<Type> orderedTypes = new()
        {
            typeof(GenerateProjectFiles),
            typeof(BuildEditorTarget),
            typeof(BuildEditor),
            typeof(LaunchEditor),
            typeof(LaunchProjectEditor),
            typeof(LaunchStandalone),
            typeof(PackageProject),
            typeof(LaunchStagedPackage),
            typeof(BuildPlugin),
            typeof(PackagePlugin),
            typeof(DeployPlugin),
            typeof(VerifyDeployment)
        };

        orderedTypes.AddRange(TypeUtils.GetSubclassesOf(typeof(Operation)));

        int sortOrder = 0;
        List<OperationDescriptor> descriptors = new();
        foreach (Type operationType in orderedTypes.Distinct())
        {
            Operation operation = Operation.CreateOperation(operationType);
            descriptors.Add(new OperationDescriptor(
                id: BuildOperationId(operationType),
                displayName: operation.OperationName,
                operationType: operationType,
                supportedTargetTypes: GetSupportedTargetTypes(operationType),
                sortOrder: sortOrder));

            sortOrder++;
        }

        return descriptors;
    }

    /// <summary>
    /// Builds a stable operation identifier from its current runtime type.
    /// </summary>
    private static string BuildOperationId(Type operationType)
    {
        return "unreal.operation." + operationType.Name;
    }

    /// <summary>
    /// Extracts the legacy generic target type used by the existing operation hierarchy so the host knows which
    /// targets are compatible with each registered operation.
    /// </summary>
    private static IReadOnlyList<Type> GetSupportedTargetTypes(Type operationType)
    {
        List<Type> supportedTargetTypes = new();
        Type? currentType = operationType;
        while (currentType != null)
        {
            if (currentType.IsGenericType && currentType.GetGenericTypeDefinition() == typeof(Operation<>))
            {
                supportedTargetTypes.Add(currentType.GetGenericArguments()[0]);
                break;
            }

            currentType = currentType.BaseType;
        }

        if (supportedTargetTypes.Count == 0)
        {
            supportedTargetTypes.Add(typeof(IOperationTarget));
        }

        return supportedTargetTypes;
    }

    /// <summary>
    /// Registers the first parity-focused set of target actions used by the Avalonia shell.
    /// </summary>
    private static void RegisterContextActions(IExtensionRegistry registry)
    {
        registry.RegisterContextAction(new ContextActionDescriptor(
            id: "unreal.target.open-directory",
            displayName: "Open Directory",
            targetType: typeof(IOperationTarget),
            execute: target => RunProcess.OpenDirectory(((IOperationTarget)target).TargetDirectory)));

        registry.RegisterContextAction(new ContextActionDescriptor(
            id: "unreal.target.open-output",
            displayName: "Open Output",
            targetType: typeof(IOperationTarget),
            execute: target => RunProcess.OpenDirectory(((IOperationTarget)target).OutputDirectory)));

        registry.RegisterContextAction(new ContextActionDescriptor(
            id: "unreal.project.open-staged-build",
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
