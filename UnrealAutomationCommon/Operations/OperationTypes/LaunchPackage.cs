using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public abstract class LaunchPackage<T> : UnrealProcessOperation<T> where T : global::LocalAutomation.Runtime.OperationTarget, IPackageProvider
    {
        /// <summary>
        /// Package launch flows expose automation settings because tests can optionally run against the launched build.
        /// </summary>
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(OperationOptionTypes.BuildConfigurationOptions));
        }

        public override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            string? baseError = base.CheckRequirementsSatisfied(operationParameters);
            if (baseError != null)
            {
                return baseError;
            }

            T target = GetRequiredTarget(typedParameters);
            Engine? engine = typedParameters.Engine;
            if (engine == null || target.GetProvidedPackage(engine) == null)
            {
                return "Provided package is null";
            }

            return null;
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
            T target = GetRequiredTarget(operationParameters);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            Package package = target.GetProvidedPackage(engine)
                ?? throw new InvalidOperationException("Launch Package requires a packaged build before command generation.");
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters));
            args.SetFlag("windowed");
            args.SetKeyValue("resx", "1920", false);
            args.SetKeyValue("resy", "1080", false);
            return new global::LocalAutomation.Runtime.Command(package.ExecutablePath, args.ToString());
        }

        protected override async Task<global::LocalAutomation.Runtime.OperationResult> ExecuteLeafAsync(CancellationToken token)
        {
            AutomationOptions automationOptions = UnrealOperationParameters.GetOptions<AutomationOptions>();
            if (automationOptions.RunTests)
            {
                T target = GetRequiredTarget(UnrealOperationParameters);
                Engine engine = GetRequiredTargetEngineInstall(UnrealOperationParameters);
                Package package = target.GetProvidedPackage(engine)
                    ?? throw new InvalidOperationException("Launch Package requires a packaged build before execution.");

                // Packages don't have a test report template, but the engine still expects it
                // Copy report template from engine to package, otherwise engine automation will error
                var reportTemplateName = "Report-Template.html";
                var reportTemplateSubdir = "Engine/Content/Automation";
                string reportTemplateSubpath = Path.Combine(reportTemplateSubdir, reportTemplateName);
                string packageDir = package.TargetDirectory;
                string reportTemplateDir = Path.Combine(packageDir, reportTemplateSubdir);
                string reportTemplatePath = Path.Combine(packageDir, reportTemplateSubpath);
                if (!File.Exists(reportTemplatePath))
                {
                    string engineReportTemplate = Path.Combine(engine.TargetPath, reportTemplateSubpath);
                    if (!File.Exists(engineReportTemplate))
                    {
                        throw new Exception("Expected engine report template");
                    }

                    Directory.CreateDirectory(reportTemplateDir);
                    File.Copy(engineReportTemplate, reportTemplatePath);
                }
            }

            return await base.ExecuteLeafAsync(token);
        }

        protected override string GetOperationName()
        {
            return "Launch Package";
        }

        public override string GetLogsPath(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            T target = GetRequiredTarget(typedParameters);
            Engine engine = GetRequiredTargetEngineInstall(typedParameters);
            Package package = target.GetProvidedPackage(engine)
                ?? throw new InvalidOperationException("Launch Package requires a packaged build before log discovery.");
            return package.LogsPath;
        }
    }

    public class LaunchPackage : LaunchPackage<Package>
    {
    }

    public class LaunchStagedPackage : LaunchPackage<Project>
    {
    }
}
