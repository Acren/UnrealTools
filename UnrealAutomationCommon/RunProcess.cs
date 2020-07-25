using System.Diagnostics;

namespace UnrealAutomationCommon
{
    public static class RunProcess
    {
        public static Process Run(ProcessStartInfo StartInfo)
        {
            Process process = new Process {StartInfo = StartInfo};
            process.Start();
            return process;
        }

        public static Process Run(string File, string Args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                Arguments = Args,
                FileName = File,
                UseShellExecute = false
            };

            return Run(startInfo);
        }

        public static Process Run(Command command)
        {
            return Run(command.File, command.Arguments);
        }

        public static Process RunAndWait(string File, string Args)
        {
            Process process = Run(File, Args);
            process.WaitForExit();
            return process;
        }

        public static void Run(string File, UnrealArguments Args)
        {
            Run(File, Args.ToString());
        }
    }
}
