using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;
using System.IO;
using Microsoft.Extensions.Logging;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class CleanProject : Operation<Project>
    {
        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            return new List<Command>();
        }

        protected override async Task<OperationResult> OnExecuted(CancellationToken token)
        {
            Logger.LogInformation("Cleaning binaries");

            Project project = GetTarget(OperationParameters);
            Engine engine = project.EngineInstance;

            // Clean targets
            string cleanPath = Path.Combine(engine.GetBuildFolder(), "BatchFiles", "Clean.bat");
            List<string> targets = new List<string>() { project.Name, project.ProjectDescriptor.EditorTargetName };
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

            return new OperationResult(true);
        }
    }
}
