using System.Diagnostics;

namespace UnrealAutomationCommon
{
    public class RunProcess
    {
        public static Process Run(string File, string Args)
        {
            ProcessStartInfo PluginBuildStartInfo = new ProcessStartInfo()
            {
                Arguments = Args,
                FileName = File,
                UseShellExecute = false
            };

            Process Process = new Process();
            Process.StartInfo = PluginBuildStartInfo;
            Process.Start();
            return Process;
        }

        public static Process Run(Command command)
        {
            return Run(command.File, command.Arguments);
        }

        public static Process RunAndWait(string File, string Args)
        {
            Process Process = Run(File, Args);
            Process.WaitForExit();
            return Process;
        }

        public static void Run(string File, UnrealArguments Args)
        {
            Run(File, Args.ToString());
        }
    }
}
