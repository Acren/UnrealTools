﻿using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public delegate void LineLoggedEventHandler(string output);

    public class LogWatcher
    {
        private StreamReader _reader;
        private string _registeredLogFile;

        public LogWatcher(Project project) : this(project, project.LogsPath)
        {
        }

        public LogWatcher(Project project, string logDirectory)
        {
            FileSystemWatcher directoryWatcher = new(logDirectory);
            directoryWatcher.Filter = project.Name + "*.log";
            directoryWatcher.Created += (Sender, Args) =>
            {
                if (ShouldRegisterLogFile(Args.FullPath))
                {
                    RegisterLogFile(Args.FullPath);
                }
            };
            directoryWatcher.Changed += (Sender, Args) =>
            {
                if (ShouldRegisterLogFile(Args.FullPath))
                {
                    RegisterLogFile(Args.FullPath);
                }

                if (Args.FullPath != _registeredLogFile)
                    // Ignore logs other than the registered one
                {
                    return;
                }

                ReadToEnd();
            };
            directoryWatcher.EnableRaisingEvents = true;
        }

        private bool HasRegisteredLogFile => _registeredLogFile != null;

        public event LineLoggedEventHandler LineLogged;

        private bool ShouldRegisterLogFile(string logFile)
        {
            return !HasRegisteredLogFile && !Path.GetFileNameWithoutExtension(logFile).Contains("-backup-");
        }

        private void RegisterLogFile(string logFile)
        {
            _registeredLogFile = logFile;

            FileStream stream = new(_registeredLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _reader = new StreamReader(stream);
            ReadToEnd();
        }

        private void ReadToEnd()
        {
            while (!_reader.EndOfStream)
            {
                string line = _reader.ReadLine();
                if (UnrealLogUtils.IsTimestampedLog(line))
                {
                    line = UnrealLogUtils.RemoveTimestamp(line);
                }

                LineLogged?.Invoke(line);
            }
        }
    }
}