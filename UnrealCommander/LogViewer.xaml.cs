using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon;

namespace UnrealCommander
{
    /// <summary>
    /// Interaction logic for LogViewer.xaml
    /// </summary>
    public partial class LogViewer : UserControl
    {
        private int LineCount = 0;
        private List<LogEntry> LogLinesBuffer = new();
        private bool scrollToEnd = true;

        public ObservableCollection<LogEntry> LogLines { get; } = new();

        public LogViewer()
        {
            InitializeComponent();

            System.Windows.Threading.DispatcherTimer dispatcherTimer = new System.Windows.Threading.DispatcherTimer();
            dispatcherTimer.Tick += (sender, args) =>
            {
                if (LogLinesBuffer.Count == 0)
                {
                    return;
                }
                foreach (LogEntry Entry in LogLinesBuffer)
                {
                    LogLines.Add(Entry);
                }
                LogLinesBuffer.Clear();

                ScrollViewer scrollViewer = ScrollViewerFinder.GetScrollViewer(DataGrid);
                if (scrollViewer != null && scrollToEnd)
                {
                    DataGrid.ScrollIntoView(LogLines.Last());
                    // UpdateLayout here appears to fix the scroll-to-end state being incorrectly lost
                    DataGrid.UpdateLayout();
                }
            };
            dispatcherTimer.Interval = TimeSpan.FromMilliseconds(100);
            dispatcherTimer.Start();
        }

        public void WriteLog(string message, LogLevel verbosity)
        {
            LineCount++;
            string finalLine = "[" + $"{DateTime.Now:u}" + "][" + LineCount + @"]: " + message;

            ScrollViewer scrollViewer = ScrollViewerFinder.GetScrollViewer(DataGrid);

            scrollToEnd = scrollViewer == null || scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 2;

            LogLinesBuffer.Add(new LogEntry { Message = finalLine, Verbosity = verbosity });
        }

        private void LogClear(object sender, RoutedEventArgs e)
        {
           LogLines.Clear();
        }
    }
}
