using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeployPlugin
{
    class DeployUtils
    {
        public static string GetPakString(bool Pak)
        {
            return Pak ? "-pak " : "";
        }

        public static string FindUPluginPath(string PluginPath)
        {
            // Get uplugin path
            string[] UPluginFiles = Directory.GetFiles(PluginPath, "*.uplugin");

            if (UPluginFiles.Length < 1)
            {
                throw new Exception("No .uplugin found in " + PluginPath);
            }

            string UPluginPath = UPluginFiles[0];
            return UPluginPath;
        }

        public static string FindPluginName(string PluginPath)
        {
            return Path.GetFileNameWithoutExtension(FindUPluginPath(PluginPath));
        }

        public static string FindHostProjectPath(string PluginPath)
        {
            // Get project path
            string ProjectPath = Path.GetFullPath(Path.Combine(PluginPath, @"..\..\")); // Up 2 levels
            return ProjectPath;
        }

        public static string FindHostUProjectPath(string PluginPath)
        {
            // Get project path
            string[] UProjectFiles;
            string ProjectPath = FindHostProjectPath(PluginPath);
            UProjectFiles = Directory.GetFiles(ProjectPath, "*.uproject");

            while (UProjectFiles.Length < 1)
            {
                if (Path.GetPathRoot(ProjectPath) == ProjectPath)
                {
                    throw new Exception("No .uproject found in " + ProjectPath);
                }

                ProjectPath = Path.GetFullPath(Path.Combine(ProjectPath, @"..\")); // Up 1 level
                UProjectFiles = Directory.GetFiles(ProjectPath, "*.uproject");
            }

            string UProjectPath = UProjectFiles[0];

            return UProjectPath;
  
        }

        public static string FindHostProjectName(string PluginPath)
        {
            return Path.GetFileNameWithoutExtension(FindHostUProjectPath(PluginPath));
        }
    }
}
