namespace UnrealAutomationCommon.Operations;

#nullable enable

/// <summary>
/// Preserves the historical UnrealAutomationCommon option wrapper type while the canonical implementation lives in the
/// shared runtime project.
/// </summary>
public class Option : global::LocalAutomation.Runtime.Option
{
}

/// <summary>
/// Preserves the historical generic option wrapper type while delegating behavior to the shared runtime model.
/// </summary>
public class Option<T> : global::LocalAutomation.Runtime.Option<T>
{
    /// <summary>
    /// Creates a typed option with the provided default value.
    /// </summary>
    public Option(T defaultValue)
        : base(defaultValue)
    {
    }

    /// <summary>
    /// Creates a typed option and invokes a callback whenever the wrapped value changes.
    /// </summary>
    public Option(System.Action changedCallback, T defaultValue)
        : base(changedCallback, defaultValue)
    {
    }

    /// <summary>
    /// Reads the wrapped value directly from the legacy option wrapper.
    /// </summary>
    public static implicit operator T(Option<T> option)
    {
        return option.Value;
    }

    /// <summary>
    /// Wraps a raw value into the legacy option wrapper type.
    /// </summary>
    public static implicit operator Option<T>(T value)
    {
        return new Option<T>(value);
    }
}

/// <summary>
/// Preserves the historical UnrealAutomationCommon option types while the canonical implementation lives in the
/// shared runtime project.
/// </summary>
public class OperationOptions : global::LocalAutomation.Runtime.OperationOptions
{
}
