using System;
using System.Diagnostics;
using System.Management;
using System.Reflection;

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
                // Prefer the runtime's built-in process-tree termination when available because it avoids platform-
                // specific child-process discovery code and works across more host configurations.
                if (TryKillProcessTree(pid))
                {
                    return;
                }

                // Fall back to the legacy WMI-based child walk on Windows desktop runtimes where System.Management is
                // available. This preserves the previous behavior for older process flows.
                ManagementObjectSearcher searcher = new("Select * From Win32_Process Where ParentProcessID=" + pid);
                ManagementObjectCollection moc = searcher.Get();
                foreach (ManagementObject mo in moc)
                {
                    KillProcessAndChildren(Convert.ToInt32(mo["ProcessID"]));
                }
            }
            catch (PlatformNotSupportedException)
            {
                // Non-desktop or cross-platform hosts may not support System.Management. In that case, terminate the
                // main process directly so cancel/terminate cannot crash the application.
            }

            TryKillSingleProcess(pid);
        }

        /// <summary>
        /// Uses reflection to call `Process.Kill(true)` when running on a runtime that exposes the cross-platform
        /// process-tree API, while keeping the shared project compatible with older target frameworks.
        /// </summary>
        private static bool TryKillProcessTree(int pid)
        {
            try
            {
                Process process = Process.GetProcessById(pid);
                MethodInfo killTreeMethod = typeof(Process).GetMethod("Kill", new[] { typeof(bool) });
                if (killTreeMethod == null)
                {
                    return false;
                }

                killTreeMethod.Invoke(process, new object[] { true });
                return true;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is PlatformNotSupportedException)
            {
                return false;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                // Process already exited.
                return true;
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
                return true;
            }
        }

        /// <summary>
        /// Terminates a single process without attempting any child-process traversal.
        /// </summary>
        private static void TryKillSingleProcess(int pid)
        {
            try
            {
                Process proc = Process.GetProcessById(pid);
                proc.Kill();
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }
        }
    }
}
