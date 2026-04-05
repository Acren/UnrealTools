using System;
using System.Collections.Generic;
using System.IO;
using LocalAutomation.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon
{
    public static class CodeUtils
    {

        public static void AddCodeModule(this IOperationTarget target, string moduleName)
        {
            JObject fileContent = JObject.Parse(File.ReadAllText(target.TargetPath));
            if (!fileContent.ContainsKey("Modules"))
            {
                throw new Exception("No Modules property");
            }

            IOperationTarget? checkTarget = target;
            while (checkTarget != null && checkTarget is not Project)
            {
                checkTarget = checkTarget.ParentTarget;
            }

            if (checkTarget is not Project project)
            {
                throw new Exception("Target must be a project or inside a project");
            }

            // Add module to uproject or uplugin
            JArray modules = fileContent["Modules"] as JArray ?? throw new Exception("Modules property must be an array");
            JObject newModule = new JObject
            {
                { "Name", moduleName },
                { "Type", "Runtime" },
                { "LoadingPhase", "Default" }
            };
            modules.Add(newModule);

            using StreamWriter file = File.CreateText(target.TargetPath);
            using JsonTextWriter writer = new(file);
            writer.Formatting = Formatting.Indented;
            writer.Indentation = 1;
            writer.IndentChar = '\u0009';
            fileContent.WriteTo(writer);

            // Create module files
            string sourceDir = Path.Combine(target.TargetDirectory, "Source");
            string sourceModuleDir = Path.Combine(sourceDir, moduleName);
            Directory.CreateDirectory(sourceModuleDir);

            Dictionary<string, string> values = new()
            {
                { "COPYRIGHT_LINE", $"// {project.GetCopyrightNotice()}" },
                { "MODULE_NAME", moduleName }
            };

            // Render files from templates
            RenderTemplate("module.build.cs", Path.Combine(sourceModuleDir, $"{moduleName}.build.cs"), values);
            RenderTemplate("module.h", Path.Combine(sourceModuleDir, "Public", $"{moduleName}.h"), values);
            RenderTemplate("module.cpp", Path.Combine(sourceModuleDir, "Private", $"{moduleName}.cpp"), values);
        }

        private static void RenderTemplate(string templateName, string outputFilePath, Dictionary<string, string> values)
        {
            string templateFileName = $"templates/{templateName}.template";
            string templateContent = File.ReadAllText(templateFileName);
            foreach (var entry in values)
            {
                templateContent = templateContent.Replace($"%{entry.Key}%", entry.Value);
            }
            string directory = Path.GetDirectoryName(outputFilePath) ?? throw new Exception("Output file path must include a directory");
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputFilePath, templateContent);
        }
    }
}
