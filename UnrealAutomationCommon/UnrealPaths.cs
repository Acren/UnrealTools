using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public static class UnrealPaths
    {
        public static string GetEditorExe(string enginePath, OperationParameters operationParameters)
        {
            return Path.Combine(enginePath, "Engine", "Binaries", "Win64", operationParameters.Configuration == BuildConfiguration.DebugGame ? "UE4Editor-Win64-DebugGame.exe" : "UE4Editor.exe");
        }
    }
}
