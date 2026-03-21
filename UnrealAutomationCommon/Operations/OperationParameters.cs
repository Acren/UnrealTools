using System.ComponentModel;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

#nullable enable

namespace UnrealAutomationCommon.Operations;

/// <summary>
/// Adds Unreal-specific environment resolution on top of the shared runtime parameter model.
/// </summary>
public class OperationParameters : global::LocalAutomation.Runtime.UnrealRuntimeOperationParameters<Engine>
{
    /// <summary>
    /// Gets the effective Unreal engine for the current target and option state.
    /// </summary>
    [JsonIgnore]
    public Engine? Engine => GetEnvironment();

    /// <summary>
    /// Preserves the existing Unreal-specific override property name while delegating storage to the runtime base.
    /// </summary>
    [JsonIgnore]
    public Engine? EngineOverride
    {
        get => EnvironmentOverride;
        set => EnvironmentOverride = value;
    }

    /// <summary>
    /// Mirrors the freeform argument string into the dedicated Unreal option set when present.
    /// </summary>
    public override string AdditionalArguments
    {
        get
        {
            AdditionalArgumentsOptions? existingOptions = GetOptionsInstance(typeof(AdditionalArgumentsOptions)) as AdditionalArgumentsOptions;
            return existingOptions?.Arguments.Value ?? base.AdditionalArguments;
        }
        set
        {
            string normalizedValue = value ?? string.Empty;
            base.AdditionalArguments = normalizedValue;

            AdditionalArgumentsOptions? existingOptions = GetOptionsInstance(typeof(AdditionalArgumentsOptions)) as AdditionalArgumentsOptions;
            if (existingOptions != null && existingOptions.Arguments.Value != normalizedValue)
            {
                existingOptions.Arguments.Value = normalizedValue;
            }
        }
    }

    /// <summary>
    /// Resolves the effective Unreal engine from explicit overrides, engine-version options, or the target itself.
    /// </summary>
    public override Engine? GetEnvironment()
    {
        if (EnvironmentOverride != null)
        {
            return EnvironmentOverride;
        }

        EngineVersionOptions? versionOptions = FindOptions<EngineVersionOptions>();
        if (versionOptions != null && versionOptions.EnabledVersions.Value.Count > 0)
        {
            EngineVersion? version = versionOptions.EnabledVersions.Value[0];
            if (version != null)
            {
                return EngineFinder.GetEngineInstall(version);
            }
        }

        if (Target is not IEngineInstanceProvider engineInstanceProvider)
        {
            return null;
        }

        return engineInstanceProvider.EngineInstance;
    }

    /// <summary>
    /// Keeps engine-derived state observable for hosts that still bind directly to this legacy type.
    /// </summary>
    protected override void OnOptionsStateChanged()
    {
        OnPropertyChanged(nameof(Engine));
    }

    /// <summary>
    /// Keeps engine-derived state observable when the active target changes.
    /// </summary>
    protected override void OnTargetStateChanged()
    {
        OnPropertyChanged(nameof(Engine));
    }
}
