using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;
using System.IO;
using Microsoft.Extensions.Logging;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class CleanProject : UnrealOperation<Project>
    {
        protected override IEnumerable<LocalAutomation.Runtime.Command> BuildCommands(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            return new List<LocalAutomation.Runtime.Command>();
        }

        protected override Task<global::LocalAutomation.Runtime.OperationResult> ExecuteLeafAsync(CancellationToken token)
        {
            Logger.LogInformation("Cleaning binaries");

            Project project = GetRequiredTarget(UnrealOperationParameters);
            Engine engine = project.EngineInstance ?? throw new InvalidOperationException("Clean Project requires a resolved engine install.");

            // Clean targets
            string cleanPath = Path.Combine(engine.GetBuildFolder(), "BatchFiles", "Clean.bat");
            string editorTargetName = project.ProjectDescriptor?.EditorTargetName
                ?? throw new InvalidOperationException("Clean Project requires a loaded project descriptor.");
            List<string> targets = new List<string>() { project.Name, editorTargetName };
            foreach (string target in targets)
            {
                foreach (BuildConfiguration configuration in EnumUtils.GetAll<BuildConfiguration>())
                {
                    string cleanParams = $"{target} Win64 {configuration} -project=\"{project.UProjectPath}\" -WaitMutex";
                    RunProcess.RunAndWait(cleanPath, cleanParams);
                }
            }

            Logger.LogInformation("Deleting intermediate folders");

            // Delete intermediate folders
            foreach (string path in Directory.GetDirectories(project.ProjectPath, "Intermediate", SearchOption.AllDirectories))
            {
                FileUtils.DeleteDirectory(path);
            }

            Logger.LogInformation("Cleaning complete");

            return Task.FromResult(new global::LocalAutomation.Runtime.OperationResult(true));
        }
    }
}
