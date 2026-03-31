using System;
using System.Collections.Generic;
using System.Text;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Creates typed execution identifiers without forcing callers to author raw string ids directly.
/// </summary>
public static class ExecutionIdentifierFactory
{
    public static ExecutionPlanId CreatePlanId(params string?[] parts)
    {
        return new ExecutionPlanId(BuildSlug(parts));
    }

    public static ExecutionTaskId CreateTaskId(ExecutionPlanId planId, params string?[] parts)
    {
        return new ExecutionTaskId(BuildSlug(Prepend(planId.Value, parts)));
    }

    public static ExecutionSessionId CreateSessionId(params string?[] parts)
    {
        return new ExecutionSessionId(BuildSlug(parts));
    }

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

    private static IEnumerable<string?> Prepend(string first, IEnumerable<string?> rest)
    {
        yield return first;
        foreach (string? item in rest)
        {
            yield return item;
        }
    }
}
