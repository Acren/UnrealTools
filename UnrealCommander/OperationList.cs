using System;
using System.Collections.Generic;
using System.Linq;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationTypes;

namespace UnrealCommander
{
    public static class OperationList
    {
        public static List<Type> GetOrderedOperationTypes()
        {
            var Result = new List<Type>
            {
                // Custom order
                typeof(GenerateProjectFiles),
                typeof(BuildEditorTarget),
                typeof(BuildEditor),
                typeof(LaunchEditor),
                typeof(LaunchStandalone),
                typeof(PackageProject),
                typeof(LaunchStagedPackage),
                typeof(BuildPlugin),
                typeof(DeployPlugin),
                typeof(VerifyDeployment)
            };

            // Add any others to the end
            Result.AddRange(TypeUtils.GetSubclassesOf(typeof(Operation)));
            return Result.Distinct().ToList();
        }
    }
}