using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Owns the Windows-native robocopy fast path for large directory copies.
/// </summary>
internal static class WindowsDirectoryCopy
{
    private const int RoboCopySuccessThreshold = 8;

    /// <summary>
    /// Attempts to copy one directory tree with robocopy, returning false when unavailable.
    /// </summary>
    public static bool TryCopy(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<string> excludedRelativePaths,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = excludedRelativePaths ?? throw new ArgumentNullException(nameof(excludedRelativePaths));
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Directory.Exists(sourcePath))
        {
            return false;
        }

        Directory.CreateDirectory(destinationPath);
        string arguments = BuildArguments(sourcePath, destinationPath, excludedRelativePaths);
        ProcessStartInfo startInfo = new()
        {
            FileName = "robocopy",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start robocopy.");
            using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() => KillProcessTree(process));
            // Read redirected streams while robocopy runs so Windows cannot block the copy on a full output pipe.
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                throw;
            }

            if (process.ExitCode < RoboCopySuccessThreshold)
            {
                return true;
            }

            string standardOutput = standardOutputTask.GetAwaiter().GetResult();
            string standardError = standardErrorTask.GetAwaiter().GetResult();
            throw new IOException(BuildFailureMessage(process.ExitCode, sourcePath, destinationPath, arguments, standardOutput, standardError));
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Terminates robocopy and any worker descendants so operation cancellation does not leave orphaned copy work.
    /// </summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process may exit between the HasExited check and Kill; either outcome satisfies cancellation.
        }
        catch (Win32Exception)
        {
            // If Windows refuses termination, the cancellation path still reports cancellation to the caller.
        }
    }

    /// <summary>
    /// Builds one robocopy command line that performs a multithreaded recursive copy while suppressing noisy progress
    /// output.
    /// </summary>
    private static string BuildArguments(string sourcePath, string destinationPath, IReadOnlyList<string> excludedRelativePaths)
    {
        List<string> arguments = new()
        {
            Quote(sourcePath),
            Quote(destinationPath),
            "/E",
            "/COPY:DAT",
            "/DCOPY:DAT",
            "/R:1",
            "/W:1",
            "/MT:16",
            "/NFL",
            "/NDL",
            "/NJH",
            "/NP"
        };

        // Robocopy supports directory and file filters in one process, so every exclude is supplied to both lists.
        if (excludedRelativePaths.Count > 0)
        {
            arguments.Add("/XD");
            foreach (string excludedRelativePath in excludedRelativePaths)
            {
                arguments.Add(Quote(Path.Combine(sourcePath, excludedRelativePath)));
            }

            arguments.Add("/XF");
            foreach (string excludedRelativePath in excludedRelativePaths)
            {
                arguments.Add(Quote(Path.Combine(sourcePath, excludedRelativePath)));
            }
        }

        return string.Join(" ", arguments);
    }

    /// <summary>
    /// Quotes one argument so spaces inside filesystem paths survive process invocation intact.
    /// </summary>
    private static string Quote(string value)
    {
        return $"\"{value}\"";
    }

    /// <summary>
    /// Builds a diagnostic exception message that preserves robocopy's own failure details for later troubleshooting.
    /// </summary>
    private static string BuildFailureMessage(int exitCode, string sourcePath, string destinationPath, string arguments, string standardOutput, string standardError)
    {
        StringBuilder message = new();
        message.Append("robocopy failed with exit code ")
            .Append(exitCode)
            .Append(" (")
            .Append(DescribeExitCode(exitCode))
            .Append(") when copying '")
            .Append(sourcePath)
            .Append("' to '")
            .Append(destinationPath)
            .Append("'. Arguments: ")
            .Append(arguments);

        // Robocopy reports copy failures on stdout in most cases, while stderr remains available for process errors.
        AppendProcessOutput(message, "Standard output", standardOutput);
        AppendProcessOutput(message, "Standard error", standardError);
        return message.ToString();
    }

    /// <summary>
    /// Appends one captured process stream only when it contains useful text.
    /// </summary>
    private static void AppendProcessOutput(StringBuilder message, string label, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        message.AppendLine()
            .Append(label)
            .AppendLine(":")
            .Append(output.Trim());
    }

    /// <summary>
    /// Decodes robocopy's bitmask exit code into the documented conditions that contributed to the result.
    /// </summary>
    private static string DescribeExitCode(int exitCode)
    {
        List<string> conditions = new();
        if ((exitCode & 1) != 0)
        {
            conditions.Add("files copied");
        }

        if ((exitCode & 2) != 0)
        {
            conditions.Add("extra files or directories detected");
        }

        if ((exitCode & 4) != 0)
        {
            conditions.Add("mismatched files or directories detected");
        }

        if ((exitCode & 8) != 0)
        {
            conditions.Add("copy failures occurred");
        }

        if ((exitCode & 16) != 0)
        {
            conditions.Add("serious robocopy error");
        }

        int unknownBits = exitCode & ~31;
        if (unknownBits != 0)
        {
            conditions.Add($"unknown bits {unknownBits}");
        }

        return conditions.Count == 0 ? "no changes" : string.Join(", ", conditions);
    }
}
