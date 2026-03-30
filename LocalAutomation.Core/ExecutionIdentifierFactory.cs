using System;
using System.Collections.Generic;
using System.Text;

namespace LocalAutomation.Core;

/// <summary>
/// Creates stable typed execution identifiers from human-readable metadata so callers never need to author raw string
/// ids directly.
/// </summary>
public static class ExecutionIdentifierFactory
{
    /// <summary>
    /// Creates a stable execution-plan identifier from the provided parts.
    /// </summary>
    public static ExecutionPlanId CreatePlanId(params string?[] parts)
    {
        return new ExecutionPlanId(BuildSlug(parts));
    }

    /// <summary>
    /// Creates a stable execution-task identifier nested under the provided plan and path parts.
    /// </summary>
    public static ExecutionTaskId CreateTaskId(ExecutionPlanId planId, params string?[] parts)
    {
        return new ExecutionTaskId(BuildSlug(Prepend(planId.Value, parts)));
    }

    /// <summary>
    /// Creates a stable execution-session identifier from the provided parts when a deterministic session identity is
    /// needed outside the default random session creation path.
    /// </summary>
    public static ExecutionSessionId CreateSessionId(params string?[] parts)
    {
        return new ExecutionSessionId(BuildSlug(parts));
    }

    /// <summary>
    /// Builds one canonical slug from the provided parts, skipping blanks and collapsing separators.
    /// </summary>
    private static string BuildSlug(IEnumerable<string?> parts)
    {
        StringBuilder builder = new();
        foreach (string? part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            foreach (char character in part)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                    continue;
                }

                if (builder.Length == 0 || builder[builder.Length - 1] == '-')
                {
                    continue;
                }

                builder.Append('-');
            }

            if (builder.Length > 0 && builder[builder.Length - 1] != '-')
            {
                builder.Append('-');
            }
        }

        string value = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Execution identifiers require at least one non-empty part.", nameof(parts));
        }

        return value;
    }

    /// <summary>
    /// Prepends the leading plan id to a later set of path parts without forcing callers to allocate the combined list.
    /// </summary>
    private static IEnumerable<string?> Prepend(string first, IEnumerable<string?> rest)
    {
        yield return first;
        foreach (string? item in rest)
        {
            yield return item;
        }
    }
}
