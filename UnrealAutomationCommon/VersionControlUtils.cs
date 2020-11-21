using System.ComponentModel;
using System.Diagnostics;

namespace UnrealAutomationCommon
{
    public class VersionControlUtils
    {
        public static string GetBranchName(string WorkingDirectory)
        {
            string gitPath = @"C:\Program Files\Git\bin\git.exe";
            ProcessStartInfo startInfo = new ProcessStartInfo(gitPath);

            startInfo.UseShellExecute = false;
            startInfo.WorkingDirectory = WorkingDirectory ?? "dir Here";
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.Arguments = "rev-parse --abbrev-ref HEAD";

            Process process = new Process();
            process.StartInfo = startInfo;

            try
            {
                process.Start();
            }
            catch (Win32Exception Ex)
            {
                if (Ex.NativeErrorCode == 2)
                {
                    throw new System.Exception("git.exe was not found or access was denied, is it installed?", Ex);
                }
                else
                {
                    throw Ex;
                }
            }

            string branchName = process.StandardOutput.ReadLine();
            return branchName;
        }
    }
}
