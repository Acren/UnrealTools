using System.IO;

namespace UnrealCommander
{
    public static class ProgramPathFinder
    {
        public static string FindPath(string programKey)
        {
            string existingPath = PersistentData.Get().GetProgramPath(programKey);
            if (existingPath != null && File.Exists(existingPath))
            {
                return existingPath;
            }
            var dialog = new Ookii.Dialogs.Wpf.VistaOpenFileDialog();
            dialog.Title = "Select executable";
            dialog.Filter = "Executable (*.exe)|*.exe";
            if (!dialog.ShowDialog() == true)
            {
                return null;
            }
            string selectedPath = dialog.FileName;
            PersistentData.Get().SetProgramPath(programKey, selectedPath);
            return selectedPath;
        }
    }
}
