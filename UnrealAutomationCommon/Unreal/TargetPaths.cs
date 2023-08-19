using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public abstract class TargetPaths<T> : SingletonBase<T> where T : class
    {
        public abstract string TargetFileExtension { get; }

        public bool IsTargetFile(string filePath)
        {
            return FileUtils.HasExtension(filePath, TargetFileExtension);
        }

        public virtual string FindTargetFile(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return null;
            }

            string[] files = Directory.GetFiles(directoryPath);
            foreach (string file in files)
            {
                if (IsTargetFile(file))
                {
                    return file;
                }
            }

            return null;
        }

        public bool IsTargetDirectory(string directoryPath)
        {
            return FindTargetFile(directoryPath) != null;
        }
    }

    public class ProjectPaths : TargetPaths<ProjectPaths>
    {
        public override string TargetFileExtension => ".uproject";
    }

    public class PluginPaths : TargetPaths<PluginPaths>
    {
        public override string TargetFileExtension => ".uplugin";
    }

    public class PackagePaths : TargetPaths<PackagePaths>
    {
        public override string TargetFileExtension => ".exe";
    }

    public class EnginePaths : TargetPaths<EnginePaths>
    {
        public override string TargetFileExtension => ".exe";

        public override string FindTargetFile(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return null;
            }

            return Path.Combine(directoryPath, "Engine/Binaries/UnrealEditor.exe");
        }
    }
}