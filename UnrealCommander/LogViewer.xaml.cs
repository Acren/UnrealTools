using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using UnrealAutomationCommon;

namespace UnrealCommander
{
    /// <summary>
    /// Interaction logic for LogViewer.xaml
    /// </summary>
    public partial class LogViewer : UserControl
    {
        private int LineCount = 0;

        public ObservableCollection<LogEntry> LogLines { get; } = new();

        public LogViewer()
        {
            InitializeComponent();
        }

        public void WriteLog(string message, LogVerbosity verbosity)
        {
            LineCount++;
            string finalLine = "[" + $"{DateTime.Now:u}" + "][" + LineCount + @"]: " + message;

            ScrollViewer scrollViewer = ScrollViewerFinder.GetScrollViewer(DataGrid);

            bool isScrolledToEnd = scrollViewer == null || scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 2;

            LogLines.Add(new LogEntry { Message = finalLine, Verbosity = verbosity });

            if (scrollViewer != null && isScrolledToEnd)
            {
                DataGrid.ScrollIntoView(LogLines.Last());
            }
        }

        private void LogClear(object sender, RoutedEventArgs e)
        {
           LogLines.Clear();
        }
    }
}
