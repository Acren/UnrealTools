using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Creates workspace descriptors for session-scoped scratch roots and stable roots that preserve reusable artifacts.
/// </summary>
public static class Workspaces
{
    // Persistent workspace metadata uses one stable JSON filename so humans and tools can identify hashed roots.
    internal const string PersistentMetadataFileName = "LocalAutomation.Workspace.json";

    // Workspace hashes stay short because Unreal-style build trees can already create very deep output paths.
    private const int PersistentHashLength = 16;

    /// <summary>
    /// Wraps one session-owned root path in the same workspace shape used by persistent roots.
    /// </summary>
    public static Workspace Session(string rootPath)
    {
        return new Workspace(rootPath, Array.Empty<ExecutionLock>(), persistentKey: null, persistentKeyParts: null, persistentKeyComponents: null);
    }

    /// <summary>
    /// Returns the persistent workspace for the provided ordered identity parts.
    /// </summary>
    public static Workspace Persistent(params string[] keyParts)
    {
        return Persistent((IEnumerable<string>)keyParts);
    }

    /// <summary>
    /// Returns the persistent workspace for the provided ordered identity parts without interpreting their meaning.
    /// </summary>
    public static Workspace Persistent(IEnumerable<string> keyParts)
    {
        return Persistent(new PersistentWorkspaceKey(keyParts));
    }

    /// <summary>
    /// Returns the persistent workspace for a key that separates hash identity from readable diagnostics.
    /// </summary>
    public static Workspace Persistent(PersistentWorkspaceKey key)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        IReadOnlyList<string> normalizedParts = NormalizeKeyParts(key.HashParts);
        IReadOnlyList<KeyValuePair<string, string>> normalizedComponents = NormalizeKeyComponents(key.Components, normalizedParts);
        string cacheKey = ComputeHash(string.Join("|", normalizedParts), PersistentHashLength);
        string workspacePath = Path.Combine(OutputPaths.TempRoot(), "PersistentWorkspaces", cacheKey);
        return new Workspace(workspacePath, new[] { new ExecutionLock($"persistent-workspace:{NormalizePathForLock(workspacePath)}") }, cacheKey, normalizedParts, normalizedComponents);
    }

    /// <summary>
    /// Validates identity parts so callers cannot accidentally collapse distinct workspaces through blank key entries.
    /// </summary>
    private static IReadOnlyList<string> NormalizeKeyParts(IEnumerable<string> keyParts)
    {
        if (keyParts == null)
        {
            throw new ArgumentNullException(nameof(keyParts));
        }

        List<string> normalizedParts = keyParts.Select(part => part?.Trim() ?? string.Empty).ToList();
        if (normalizedParts.Count == 0)
        {
            throw new ArgumentException("At least one persistent workspace key part is required.", nameof(keyParts));
        }

        if (normalizedParts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Persistent workspace key parts cannot be null, empty, or whitespace.", nameof(keyParts));
        }

        return normalizedParts;
    }

    /// <summary>
    /// Validates readable key components, falling back to numbered hash parts when callers provide no labels.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> NormalizeKeyComponents(IEnumerable<KeyValuePair<string, string>>? components, IReadOnlyList<string> normalizedParts)
    {
        List<KeyValuePair<string, string>> normalizedComponents = components?
            .Select(component => new KeyValuePair<string, string>(component.Key?.Trim() ?? string.Empty, component.Value?.Trim() ?? string.Empty))
            .ToList()
            ?? new List<KeyValuePair<string, string>>();
        if (normalizedComponents.Any(component => string.IsNullOrWhiteSpace(component.Key) || string.IsNullOrWhiteSpace(component.Value)))
        {
            throw new ArgumentException("Persistent workspace key component names and values cannot be null, empty, or whitespace.", nameof(components));
        }

        if (normalizedComponents.Count > 0)
        {
            return normalizedComponents;
        }

        return normalizedParts
            .Select((part, index) => new KeyValuePair<string, string>($"Part {index + 1}", part))
            .ToArray();
    }

    /// <summary>
    /// Produces a compact uppercase hash for a validated workspace identity string.
    /// </summary>
    private static string ComputeHash(string source, int length)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash).Substring(0, length);
    }

    /// <summary>
    /// Normalizes workspace paths before they become execution-lock keys so equivalent Windows paths serialize together.
    /// </summary>
    private static string NormalizePathForLock(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
    }
}

/// <summary>
/// Separates stable persistent-workspace hash inputs from labels that make the resulting hashed directory explainable.
/// </summary>
public sealed class PersistentWorkspaceKey
{
    /// <summary>
    /// Creates one persistent-workspace key from raw hash parts and optional readable components.
    /// </summary>
    public PersistentWorkspaceKey(IEnumerable<string> hashParts, IEnumerable<KeyValuePair<string, string>>? components = null)
    {
        HashParts = (hashParts ?? throw new ArgumentNullException(nameof(hashParts))).ToArray();
        Components = components?.ToArray() ?? Array.Empty<KeyValuePair<string, string>>();
    }

    /// <summary>
    /// Gets the ordered values that define the workspace hash and therefore the root directory name.
    /// </summary>
    public IReadOnlyList<string> HashParts { get; }

    /// <summary>
    /// Gets readable key components written to logs and metadata files for humans inspecting persistent workspace roots.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Components { get; }
}

/// <summary>
/// Describes one filesystem workspace root plus any locks required while mutating that root.
/// </summary>
public sealed class Workspace
{
    // Stores the persistent workspace hash when this descriptor represents a reusable workspace root.
    private readonly string? _persistentKey;
    // Stores the exact normalized hash inputs so metadata can explain which values produced the directory hash.
    private readonly IReadOnlyList<string> _persistentKeyParts;
    // Stores readable labels for the hash inputs so logs and metadata identify the workspace's purpose.
    private readonly IReadOnlyList<KeyValuePair<string, string>> _persistentKeyComponents;

    /// <summary>
    /// Creates one immutable workspace descriptor from a root path and its mutation locks.
    /// </summary>
    internal Workspace(string rootPath, IReadOnlyList<ExecutionLock> mutationLocks, string? persistentKey, IReadOnlyList<string>? persistentKeyParts, IReadOnlyList<KeyValuePair<string, string>>? persistentKeyComponents)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath) ? throw new ArgumentException("Workspace root path is required.", nameof(rootPath)) : rootPath;
        MutationLocks = (mutationLocks ?? throw new ArgumentNullException(nameof(mutationLocks))).ToArray();
        _persistentKey = persistentKey;
        _persistentKeyParts = persistentKeyParts?.ToArray() ?? Array.Empty<string>();
        _persistentKeyComponents = persistentKeyComponents?.ToArray() ?? Array.Empty<KeyValuePair<string, string>>();
    }

    /// <summary>
    /// Gets the directory root where callers materialize inputs, run operations, and read outputs.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the locks callers should hold while mutating this workspace root.
    /// </summary>
    public IReadOnlyList<ExecutionLock> MutationLocks { get; }

    /// <summary>
    /// Ensures the workspace directory exists and records observable metadata for persistent workspace roots.
    /// </summary>
    public void EnsureReady(ILogger logger)
    {
        if (logger == null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        /* Directory existence is checked before creation so logs can distinguish warm workspace reuse from a new hashed
           root without relying on callers to track that state separately. */
        bool reusedExistingWorkspace = Directory.Exists(RootPath);
        Directory.CreateDirectory(RootPath);
        if (_persistentKey == null)
        {
            return;
        }

        string metadataPath = GetPath(Workspaces.PersistentMetadataFileName);
        string lifecycleVerb = reusedExistingWorkspace ? "Reusing" : "Created";
        logger.LogInformation("{LifecycleVerb} persistent workspace '{WorkspacePath}' for key '{WorkspaceKey}'.", lifecycleVerb, RootPath, _persistentKey);
        logger.LogInformation("Persistent workspace key components: {WorkspaceKeyComponents}", FormatComponentsForLog());
        WriteMetadataFile(metadataPath, lifecycleVerb, logger);
    }

    /// <summary>
    /// Combines relative path segments under the workspace root, or returns the root when no segments are supplied.
    /// </summary>
    public string GetPath(params string[] segments)
    {
        if (segments == null)
        {
            throw new ArgumentNullException(nameof(segments));
        }

        return segments.Aggregate(RootPath, Path.Combine);
    }

    /// <summary>
    /// Formats readable key components as one compact log value that stays searchable in session logs.
    /// </summary>
    private string FormatComponentsForLog()
    {
        return string.Join("; ", _persistentKeyComponents.Select(component => $"{component.Key}={component.Value}"));
    }

    /// <summary>
    /// Writes the human-readable metadata file, warning rather than failing if diagnostics cannot be persisted.
    /// </summary>
    private void WriteMetadataFile(string metadataPath, string lifecycleVerb, ILogger logger)
    {
        try
        {
            File.WriteAllText(metadataPath, BuildMetadataFileContent(lifecycleVerb));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(ex, "Failed to write persistent workspace metadata file '{MetadataPath}'.", metadataPath);
        }
    }

    /// <summary>
    /// Builds structured JSON metadata that explains a hashed persistent workspace root.
    /// </summary>
    private string BuildMetadataFileContent(string lifecycleVerb)
    {
        // A fresh timestamp records when this workspace identity was last observed during an operation run.
        DateTimeOffset observedAt = DateTimeOffset.Now;
        // Anonymous projection keeps the persisted document shape explicit without creating a separate persistence model.
        object metadata = new
        {
            workspacePath = RootPath,
            workspaceKey = _persistentKey ?? string.Empty,
            lastObservedState = lifecycleVerb,
            lastObservedAt = observedAt,
            keyComponents = _persistentKeyComponents.Select(component => new
            {
                name = component.Key,
                value = component.Value
            }),
            rawHashParts = _persistentKeyParts
        };
        // Indented JSON stays readable in Explorer/editor previews while remaining structured for tooling.
        return JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }
}
