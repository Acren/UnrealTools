using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LocalAutomation.Runtime;

/// <summary>
/// Centralizes compact filesystem naming for execution-session temp paths so runtime-generated workspace folders stay
/// readable while fitting within Windows path-length constraints.
/// </summary>
public static class ExecutionPathConventions
{
    private const int DefaultMaxSegmentLength = 16;
    private const int SessionIdPathLength = 8;
    private const int HashSuffixLength = 4;

    /// <summary>
    /// Returns a compact filesystem-safe identifier for one execution session.
    /// </summary>
    public static string GetSessionPathId(ExecutionSessionId sessionId)
    {
        string value = sessionId.Value;
        return value.Length <= SessionIdPathLength
            ? value
            : value.Substring(0, SessionIdPathLength);
    }

    /// <summary>
     /// Returns one compact filesystem-safe segment derived from a human-readable label.
     /// </summary>
    public static string MakeCompactSegment(string source, int maxLength = DefaultMaxSegmentLength)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Compact path source must not be empty.", nameof(source));
        }

        if (maxLength < 6)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Compact path segments must allow enough room for a readable prefix and hash suffix.");
        }

        /* Execution temp paths should preserve recognizable names without keeping whitespace or punctuation that only
           increases path length. Compacting to alphanumeric characters keeps folder names stable and readable while
           leaving enough path budget for Unreal's deep intermediate output structure. */
        string compact = new string(source.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(compact))
        {
            compact = "Path";
        }

        if (compact.Length <= maxLength)
        {
            return compact;
        }

        /* Long labels are truncated with a stable hash suffix so two different long names do not silently collapse onto
           the same on-disk folder segment. */
        string hash = GetStableHashSuffix(source, HashSuffixLength);
        int prefixLength = maxLength - hash.Length;
        return compact.Substring(0, prefixLength) + hash;
    }

    /// <summary>
    /// Generates one stable short hash suffix for compacted path segments.
    /// </summary>
    private static string GetStableHashSuffix(string source, int length)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source));
        string hex = BitConverter.ToString(hash).Replace("-", string.Empty, StringComparison.Ordinal);
        return hex.Substring(0, length);
    }
}
