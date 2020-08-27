using System.Diagnostics;

namespace UnrealAutomationCommon
{
    public class VersionControlUtils
    {
        public static string GetBranchName(string WorkingDirectory)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("git.exe");

            startInfo.UseShellExecute = false;
            startInfo.WorkingDirectory = WorkingDirectory ?? "dir Here";
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.Arguments = "rev-parse --abbrev-ref HEAD";

            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            string branchName = process.StandardOutput.ReadLine();
            return branchName;
        }
    }
}
