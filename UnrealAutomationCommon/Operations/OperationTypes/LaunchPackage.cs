using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public abstract class LaunchPackage<T> : UnrealProcessOperation<T> where T : OperationTarget, IPackageProvider
    {
        public override string CheckRequirementsSatisfied(OperationParameters operationParameters)
        {
            string baseError = base.CheckRequirementsSatisfied(operationParameters);
            if (baseError != null) return baseError;

            if (GetTarget(operationParameters).ProvidedPackage == null) return "Provided package is null";

            return null;
        }

        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters));
            args.SetFlag("windowed");
            args.SetKeyValue("resx", "1920", false);
            args.SetKeyValue("resy", "1080", false);
            return new Command(GetTarget(operationParameters).ProvidedPackage.ExecutablePath, args);
        }

        protected override async Task<OperationResult> OnExecuted(CancellationToken token)
        {
            AutomationOptions automationOptions = OperationParameters.FindOptions<AutomationOptions>();
            if (automationOptions is { RunTests: { Value: true } })
            {
                // Packages don't have a test report template, but the engine still expects it
                // Copy report template from engine to package, otherwise engine automation will error
                var reportTemplateName = "Report-Template.html";
                var reportTemplateSubdir = "Engine/Content/Automation";
                string reportTemplateSubpath = Path.Combine(reportTemplateSubdir, reportTemplateName);
                string packageDir = GetTarget(OperationParameters).ProvidedPackage.TargetDirectory;
                string reportTemplateDir = Path.Combine(packageDir, reportTemplateSubdir);
                string reportTemplatePath = Path.Combine(packageDir, reportTemplateSubpath);
                if (!File.Exists(reportTemplatePath))
                {
                    string engineReportTemplate = Path.Combine((GetTarget(OperationParameters) as IEngineInstallProvider).EngineInstall.InstallDirectory, reportTemplateSubpath);
                    if (!File.Exists(engineReportTemplate)) throw new Exception("Expected engine report template");
                    Directory.CreateDirectory(reportTemplateDir);
                    File.Copy(engineReportTemplate, reportTemplatePath);
                }
            }

            return await base.OnExecuted(token);
        }

        protected override string GetOperationName()
        {
            return "Launch Package";
        }

        public override string GetLogsPath(OperationParameters operationParameters)
        {
            return GetTarget(operationParameters).ProvidedPackage.LogsPath;
        }
    }

    public class LaunchPackage : LaunchPackage<Package>
    {
    }

    public class LaunchStagedPackage : LaunchPackage<Project>
    {
    }
}