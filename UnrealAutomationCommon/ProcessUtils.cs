using System;
using System.Diagnostics;

namespace UnrealAutomationCommon
{
    public static class ProcessUtils
    {
        /// <summary>
        /// Terminates the provided process and, when the runtime supports it, its child-process tree.
        /// </summary>
        public static void KillProcessAndChildren(Process process)
        {
            if (process == null)
            {
                throw new ArgumentNullException(nameof(process));
            }

            KillProcessAndChildren(process.Id);
        }

        /// <summary>
        /// Terminates the provided process ID and attempts to include child processes without relying solely on the
        /// Windows-only WMI path.
        /// </summary>
        public static void KillProcessAndChildren(int pid)
        {
            // Cannot close 'system idle process'.
            if (pid == 0)
            {
                return;
            }

            try
            {
                /* .NET 10 exposes cross-platform process-tree termination directly, so the runtime can own child-process
                   traversal without reflection or Windows-only WMI queries. */
                Process process = Process.GetProcessById(pid);
                process.Kill(true);
                return;
            }
            catch (ArgumentException)
            {
                // Process already exited.
                return;
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
                return;
            }
        }
    }
}
