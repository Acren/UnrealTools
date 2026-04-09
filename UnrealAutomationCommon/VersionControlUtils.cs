using System;
using System.ComponentModel;
using System.Diagnostics;

namespace UnrealAutomationCommon
{
    public class VersionControlUtils
    {
        /// <summary>
        /// Returns the current git branch for one working tree while keeping the child process fully backgrounded.
        /// </summary>
        public static string GetBranchName(string WorkingDirectory)
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

            string branchName = process.StandardOutput.ReadLine();
            /* Waiting for exit keeps the backgrounded git process lifecycle tidy and ensures stderr has drained before
               control returns to the caller. */
            process.WaitForExit();
            return branchName;
        }
    }
}
