using System;
using System.Diagnostics;
using System.IO;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon
{
    public static class RunProcess
    {
        public static Process Run(ProcessStartInfo StartInfo)
        {
            Process process = new() { StartInfo = StartInfo };
            process.Start();
            return process;
        }

        public static Process Run(string File, string Args)
        {
            if (File == null)
            {
                throw new ArgumentNullException(nameof(File));
            }

            ProcessStartInfo startInfo = new()
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

        public static void OpenDirectory(string DirectoryPath)
        {
            Directory.CreateDirectory(DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = DirectoryPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }
    }
}