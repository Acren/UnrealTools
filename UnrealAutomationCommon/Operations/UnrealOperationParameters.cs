using System.ComponentModel;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

#nullable enable

namespace UnrealAutomationCommon.Operations;

/// <summary>
/// Adds Unreal-specific environment resolution on top of the shared runtime parameter model.
/// </summary>
public class UnrealOperationParameters : global::LocalAutomation.Runtime.OperationParameters
{
    private Engine? _engineOverride;

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
        get => _engineOverride;
        set => _engineOverride = value;
    }

    /// <summary>
    /// Mirrors the freeform argument string into the dedicated Unreal option set when present.
    /// </summary>
    public override string AdditionalArguments
    {
        get
        {
            AdditionalArgumentsOptions? existingOptions = GetOptionsInstance(typeof(AdditionalArgumentsOptions)) as AdditionalArgumentsOptions;
            return existingOptions?.Arguments ?? base.AdditionalArguments;
        }
        set
        {
            string normalizedValue = value ?? string.Empty;
            base.AdditionalArguments = normalizedValue;

            AdditionalArgumentsOptions? existingOptions = GetOptionsInstance(typeof(AdditionalArgumentsOptions)) as AdditionalArgumentsOptions;
            if (existingOptions != null && existingOptions.Arguments != normalizedValue)
            {
                existingOptions.Arguments = normalizedValue;
            }
        }
    }

    /// <summary>
    /// Resolves the effective Unreal engine from explicit overrides, engine-version options, or the target itself.
    /// </summary>
    public Engine? GetEnvironment()
    {
        if (_engineOverride != null)
        {
            return _engineOverride;
        }

        // Unreal operations now preregister engine-version selection up front, so engine resolution should fail loudly
        // if a caller tries to rely on the override without declaring it.
        EngineVersionOptions versionOptions = GetOptions<EngineVersionOptions>();
        if (versionOptions.EnabledVersions.Count > 0)
        {
            EngineVersion? version = versionOptions.EnabledVersions[0];
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
    /// Copies Unreal-specific engine overrides onto child parameter sets.
    /// </summary>
    public override global::LocalAutomation.Runtime.OperationParameters CreateChild()
    {
        UnrealOperationParameters child = (UnrealOperationParameters)base.CreateChild();
        child.EngineOverride = EngineOverride;
        return child;
    }

    /// <summary>
    /// Keeps engine-derived state observable for hosts that still bind directly to this legacy type.
    /// </summary>
    protected override void OnOptionsStateChanged()
    {
        OnPropertyChanged(nameof(Engine));
    }

    /// <summary>
    /// Returns whether the current engine-version selection fits the expected single-engine override shape used by
    /// operations that can also fall back to the target-derived engine.
    /// </summary>
    public bool HasValidSingleEngineSelection()
    {
        return GetOptions<EngineVersionOptions>().EnabledVersions.Count <= 1;
    }

    /// <summary>
    /// Returns the validation message for single-engine operations when too many explicit engine overrides are selected.
    /// </summary>
    public string? GetSingleEngineSelectionValidationMessage()
    {
        if (HasValidSingleEngineSelection())
        {
            return null;
        }

        return "Select at most one engine version, or clear the selection to use the target engine";
    }

    /// <summary>
    /// Keeps engine-derived state observable when the active target changes.
    /// </summary>
    protected override void OnTargetStateChanged()
    {
        OnPropertyChanged(nameof(Engine));
    }
}
