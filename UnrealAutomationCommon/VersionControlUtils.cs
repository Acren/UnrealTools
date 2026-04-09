using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace UnrealAutomationCommon
{
    public class VersionControlUtils
    {
        /// <summary>
        /// Returns the current git branch for one working tree while keeping the child process fully backgrounded.
        /// </summary>
        public static async Task<string> GetBranchNameAsync(string WorkingDirectory)
        {
            var gitPath = @"C:\Program Files\Git\bin\git.exe";
            ProcessStartInfo startInfo = new(gitPath);

            startInfo.UseShellExecute = false;
            startInfo.WorkingDirectory = WorkingDirectory ?? "dir Here";
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.Arguments = "branch --show-current";

            using Process process = new();
            process.StartInfo = startInfo;
            try
            {
                process.Start();
            }
            catch (Win32Exception Ex)
            {
                if (Ex.NativeErrorCode == 2)
                {
                    throw new Exception("git.exe was not found or access was denied, is it installed?", Ex);
                }

                throw;
            }

            /* Read stdout and stderr asynchronously so the caller does not block a worker thread while git resolves the
               current branch and so non-zero exits can report the captured error text clearly. */
            Task<string?> branchNameTask = process.StandardOutput.ReadLineAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string branchName = await branchNameTask ?? string.Empty;
            string error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new Exception($"git branch --show-current failed with exit code {process.ExitCode}. Error: {error}");
            }

            return branchName;
        }
    }
}
