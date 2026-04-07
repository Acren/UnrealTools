using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace LocalAutomation.Core.IO;

/// <summary>
/// Owns the Windows-native robocopy fast path for large directory copies.
/// </summary>
internal static class WindowsDirectoryCopy
{
    private const int RoboCopySuccessThreshold = 8;

    /// <summary>
    /// Attempts to copy one directory tree with robocopy and returns false only when the tool is unavailable or the
    /// current platform is not Windows.
    /// </summary>
    public static bool TryCopy(string sourcePath, string destinationPath)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Directory.Exists(sourcePath))
        {
            return false;
        }

        Directory.CreateDirectory(destinationPath);
        ProcessStartInfo startInfo = new()
        {
            FileName = "robocopy",
            Arguments = BuildArguments(sourcePath, destinationPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start robocopy.");
            process.WaitForExit();

            if (process.ExitCode < RoboCopySuccessThreshold)
            {
                return true;
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            throw new IOException($"robocopy failed with exit code {process.ExitCode} when copying '{sourcePath}' to '{destinationPath}'. Output: {output} Error: {error}");
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds one robocopy command line that performs a multithreaded recursive copy while suppressing noisy progress
    /// output.
    /// </summary>
    private static string BuildArguments(string sourcePath, string destinationPath)
    {
        return string.Join(" ",
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
            "/NP");
    }

    /// <summary>
    /// Quotes one argument so spaces inside filesystem paths survive process invocation intact.
    /// </summary>
    private static string Quote(string value)
    {
        return $"\"{value}\"";
    }
}
