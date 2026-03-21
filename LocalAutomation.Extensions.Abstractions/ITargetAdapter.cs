namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Exposes the stable target information UI hosts need without forcing the application layer to reflect over
/// extension-owned runtime target types.
/// </summary>
public interface ITargetAdapter
{
    /// <summary>
    /// Gets the stable adapter identifier used for duplicate-registration checks and diagnostics.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Returns whether this adapter can inspect the provided runtime target instance.
    /// </summary>
    bool CanAdapt(object target);

    /// <summary>
    /// Returns whether the provided runtime target is currently valid.
    /// </summary>
    bool IsValid(object target);

    /// <summary>
    /// Returns the user-facing display name for the provided runtime target.
    /// </summary>
    string GetDisplayName(object target);

    /// <summary>
    /// Returns the user-facing type name for the provided runtime target.
    /// </summary>
    string GetTypeName(object target);

    /// <summary>
    /// Returns the stable target path or source string for the provided runtime target.
    /// </summary>
    string GetTargetPath(object target);
}
