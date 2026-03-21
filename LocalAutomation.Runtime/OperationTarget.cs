using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace LocalAutomation.Runtime;

/// <summary>
/// Describes the minimum shared runtime contract for a selectable automation target.
/// </summary>
public interface IOperationTarget : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the short runtime name for the target.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the user-facing display name for the target.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the stable path used to identify the target.
    /// </summary>
    string TargetPath { get; }

    /// <summary>
    /// Gets the directory that contains the target's primary files.
    /// </summary>
    string TargetDirectory { get; }

    /// <summary>
    /// Gets the shared output directory associated with the target.
    /// </summary>
    string OutputDirectory { get; }

    /// <summary>
    /// Gets the human-readable target type label.
    /// </summary>
    string TypeName { get; }

    /// <summary>
    /// Gets or sets the optional test label used when reporting automation results.
    /// </summary>
    string TestName { get; set; }

    /// <summary>
    /// Gets whether the target currently resolves to valid underlying data on disk.
    /// </summary>
    bool IsValid { get; }

    /// <summary>
    /// Gets the parent target when the runtime target hierarchy is nested.
    /// </summary>
    IOperationTarget? ParentTarget { get; }

    /// <summary>
    /// Walks the parent chain to return the root-most target in the hierarchy.
    /// </summary>
    IOperationTarget RootTarget
    {
        get
        {
            IOperationTarget currentRoot = this;
            while (true)
            {
                IOperationTarget? parent = currentRoot.ParentTarget;
                if (parent == null)
                {
                    return currentRoot;
                }

                currentRoot = parent;
            }
        }
    }

    /// <summary>
    /// Gets whether this target has no parent target.
    /// </summary>
    bool IsRoot { get; }
}

/// <summary>
/// Provides the shared default implementation for runtime targets.
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public abstract class OperationTarget : IOperationTarget
{
    private string _testName = string.Empty;

    /// <summary>
    /// Gets the serialized target path used to restore the target later.
    /// </summary>
    [JsonProperty]
    public string TargetPath { get; protected set; } = string.Empty;

    /// <summary>
    /// Gets the runtime name for the target.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the default user-facing label, which matches the runtime name unless a subtype overrides it.
    /// </summary>
    public virtual string DisplayName => Name;

    /// <summary>
    /// Gets the optional parent target in the runtime target hierarchy.
    /// </summary>
    public virtual IOperationTarget? ParentTarget => null;

    /// <summary>
    /// Gets the directory containing the target's primary files.
    /// </summary>
    public string TargetDirectory => TargetPath;

    /// <summary>
    /// Gets the leaf folder name for the current target path when one is available.
    /// </summary>
    public string? DirectoryName => TargetDirectory != null ? new DirectoryInfo(TargetDirectory).Name : null;

    /// <summary>
    /// Gets whether this target is the root of its hierarchy.
    /// </summary>
    public bool IsRoot => ParentTarget == null;

    /// <summary>
    /// Gets or sets the optional test name used by reporting flows.
    /// </summary>
    [JsonProperty]
    public string TestName
    {
        get => _testName;
        set
        {
            _testName = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the shared output directory for this target.
    /// </summary>
    public string OutputDirectory => Path.Combine(OutputPaths.Root(), Name.Replace(" ", string.Empty));

    /// <summary>
    /// Gets a human-readable target type label derived from the runtime type name.
    /// </summary>
    public string TypeName => SplitWordsByUppercase(GetType().Name);

    /// <summary>
    /// Gets whether the target currently resolves to valid underlying data.
    /// </summary>
    public virtual bool IsValid => true;

    /// <summary>
    /// Raised when one of the target's observable properties changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Reloads descriptor-backed state from disk when the concrete target supports it.
    /// </summary>
    public abstract void LoadDescriptor();

    /// <summary>
    /// Raises the property-changed event for the provided property name.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Compares targets by runtime type and stable path so deserialized targets can match live instances.
    /// </summary>
    public override bool Equals(object? other)
    {
        if (other == null || other.GetType() != GetType())
        {
            return false;
        }

        return ((OperationTarget)other).TargetPath == TargetPath;
    }

    /// <summary>
    /// Keeps the hash code aligned with the stable target path equality behavior.
    /// </summary>
    public override int GetHashCode()
    {
        return TargetPath?.GetHashCode() ?? 0;
    }

    /// <summary>
    /// Expands PascalCase type names into a spaced label for UI display.
    /// </summary>
    private static string SplitWordsByUppercase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        using StringWriter writer = new();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 && char.IsUpper(current))
            {
                writer.Write(' ');
            }

            writer.Write(current);
        }

        return writer.ToString();
    }
}
