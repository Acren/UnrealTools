using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnrealAutomationCommon.Operations
{
    public class PackageProject : Operation
    {
        public override Command GetCommand(OperationParameters operationParameters)
        {
            Arguments Arguments = UATArguments.MakeArguments(operationParameters);
            Arguments.AddFlag("cook");
            Arguments.AddFlag("stage");
            Arguments.AddFlag("pak");
            //Arguments.AddFlag("archive");
            //Arguments.AddPath("archivedirectory", Path.Combine("C:/",operationParameters.Project.Name));
            //Arguments = "BuildCookRun -project=\"" + ExampleProjectBuildUProjectPath + "\" -noP4 -platform=Win64 -clientconfig=Development -serverconfig=Development -cook -allmaps -build -stage " + DeployUtils.GetPakString(Params.Pak) + "-archive -archivedirectory=\"" + InstalledPluginTestBuildArchivePath + "\"",
            return new Command(operationParameters.Project.ProjectDefinition.GetRunUAT(), Arguments);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
