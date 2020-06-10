using System.Collections.ObjectModel;
using UnrealAutomationCommon;

namespace UnrealLauncher
{
    class ProjectSource
    {
        public static ObservableCollection<Project> Projects = new ObservableCollection<Project>();

        public static void LoadProjects()
        {

        }

        public static Project AddProject(string Path)
        {
            Project NewProject = new Project(Path);
            Projects.Add(NewProject);
            return NewProject;
        }
    }
}
