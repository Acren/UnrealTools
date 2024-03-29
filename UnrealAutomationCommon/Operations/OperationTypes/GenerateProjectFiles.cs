﻿using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class GenerateProjectFiles : CommandProcessOperation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new();
            args.SetFlag("projectfiles");
            args.SetKeyPath("project", GetTarget(operationParameters).UProjectPath);
            args.SetFlag("game");
            args.SetFlag("rocket");
            args.SetFlag("progress");
            args.AddAdditionalArguments(operationParameters);
            return new Command(GetTargetEngineInstall(operationParameters).GetUBTExe(), args);
        }
    }
}