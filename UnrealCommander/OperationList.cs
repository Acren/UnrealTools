﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander
{
    public static class OperationList
    {
        public static List<Type> GetOrderedOperationTypes()
        {
            List<Type> Result = new List<Type>()
            {
                // Custom order
                typeof(GenerateProjectFiles),
                typeof(BuildEditorTarget),
                typeof(BuildEditor),
                typeof(LaunchEditor),
                typeof(LaunchStandalone),
                typeof(PackageProject),
                typeof(LaunchStagedPackage),
                typeof(BuildPlugin)
            };

            // Add any others to the end
            Result.AddRange(TypeUtils.GetSubclassesOf(typeof(Operation)));
            return Result.Distinct().ToList();
        }
    }
}
