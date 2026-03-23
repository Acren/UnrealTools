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
            optionSetTypes.Add(typeof(AutomationOptions));
        }

        public override string CheckRequirementsSatisfied(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            string baseError = base.CheckRequirementsSatisfied(operationParameters);
            if (baseError != null)
            {
                return baseError;
            }

            if (GetTarget(typedParameters).GetProvidedPackage(typedParameters.Engine) == null)
            {
                return "Provided package is null";
            }

            return null;
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters));
            args.SetFlag("windowed");
            args.SetKeyValue("resx", "1920", false);
            args.SetKeyValue("resy", "1080", false);
            return new global::LocalAutomation.Runtime.Command(GetTarget(operationParameters).GetProvidedPackage(operationParameters.Engine).ExecutablePath, args.ToString());
        }

        protected override async Task<global::LocalAutomation.Runtime.OperationResult> OnExecuted(CancellationToken token)
        {
            AutomationOptions automationOptions = UnrealOperationParameters.FindOptions<AutomationOptions>();
            if (automationOptions is { RunTests: { Value: true } })
            {
                // Packages don't have a test report template, but the engine still expects it
                // Copy report template from engine to package, otherwise engine automation will error
                var reportTemplateName = "Report-Template.html";
                var reportTemplateSubdir = "Engine/Content/Automation";
                string reportTemplateSubpath = Path.Combine(reportTemplateSubdir, reportTemplateName);
                string packageDir = GetTarget(UnrealOperationParameters).GetProvidedPackage(UnrealOperationParameters.Engine).TargetDirectory;
                string reportTemplateDir = Path.Combine(packageDir, reportTemplateSubdir);
                string reportTemplatePath = Path.Combine(packageDir, reportTemplateSubpath);
                if (!File.Exists(reportTemplatePath))
                {
                    string engineReportTemplate = Path.Combine(GetTargetEngineInstall(UnrealOperationParameters).TargetPath, reportTemplateSubpath);
                    if (!File.Exists(engineReportTemplate))
                    {
                        throw new Exception("Expected engine report template");
                    }

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

        public override string GetLogsPath(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            return GetTarget(typedParameters).GetProvidedPackage(typedParameters.Engine).LogsPath;
        }
    }

    public class LaunchPackage : LaunchPackage<Package>
    {
    }

    public class LaunchStagedPackage : LaunchPackage<Project>
    {
    }
}
