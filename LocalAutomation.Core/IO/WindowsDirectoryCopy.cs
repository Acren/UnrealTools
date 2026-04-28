using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start robocopy.");
            using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() => KillProcessTree(process));
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

            throw new IOException($"robocopy failed with exit code {process.ExitCode} when copying '{sourcePath}' to '{destinationPath}'. Arguments: {arguments}");
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
            "/NJS",
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
}
