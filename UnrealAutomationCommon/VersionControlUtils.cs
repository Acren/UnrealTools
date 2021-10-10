using System;
using System.ComponentModel;
using System.Diagnostics;

namespace UnrealAutomationCommon
{
    public class VersionControlUtils
    {
        public static string GetBranchName(string WorkingDirectory)
        {
            var gitPath = @"C:\Program Files\Git\bin\git.exe";
            ProcessStartInfo startInfo = new(gitPath);

            startInfo.UseShellExecute = false;
            startInfo.WorkingDirectory = WorkingDirectory ?? "dir Here";
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.Arguments = "rev-parse --abbrev-ref HEAD";

            Process process = new();
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

                throw Ex;
            }

            string branchName = process.StandardOutput.ReadLine();
            return branchName;
        }
    }
}