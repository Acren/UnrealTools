using System.IO;
using LocalAutomation.Core.IO;

namespace UnrealAutomationCommon.Unreal
{
    public abstract class TargetPaths<T> : SingletonBase<T> where T : class
    {
        public abstract string TargetFileExtension { get; }

        // Callers that construct runtime targets should use the required lookup so invalid directories fail fast instead
        // of propagating empty placeholder paths through the rest of the automation flow.
        public string FindRequiredTargetFile(string directoryPath)
        {
            return FindTargetFile(directoryPath)
                ?? throw new FileNotFoundException($"Could not find target file '*{TargetFileExtension}' in '{directoryPath}'.", directoryPath);
        }

        public bool IsTargetFile(string filePath)
        {
            return FileUtils.HasExtension(filePath, TargetFileExtension);
        }

        public virtual string? FindTargetFile(string directoryPath)
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

        public override string? FindTargetFile(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return null;
            }

            string unrealEditorPath = Path.Combine(directoryPath, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
            if (File.Exists(unrealEditorPath))
            {
                return unrealEditorPath;
            }

            string ue4EditorPath = Path.Combine(directoryPath, "Engine", "Binaries", "Win64", "UE4Editor.exe");
            return File.Exists(ue4EditorPath) ? ue4EditorPath : null;
        }
    }
}
