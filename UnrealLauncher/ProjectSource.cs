using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace UnrealLauncher
{
    class ProjectSource
    {
        public static ObservableCollection<Project> Projects = new ObservableCollection<Project>();

        public static void LoadProjects()
        {

        }

        public static Project AddProject()
        {
            Project NewProject = new Project();
            Projects.Add(NewProject);
            return NewProject;
        }
    }
}
