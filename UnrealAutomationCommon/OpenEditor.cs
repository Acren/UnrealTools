using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnrealAutomationCommon
{
    public class OpenEditor
    {
        public static void Open(string EnginePath, string UProjectPath, UnrealArguments Args)
        {
            RunProcess.Run(GetFileString(EnginePath), GetArgsString(UProjectPath, Args));
        }

        public static string GetArgsString(string UProjectPath, UnrealArguments Args)
        {
            string ArgsString = "\"" + UProjectPath + "\"";
            UnrealArguments.Combine(ref ArgsString, Args.ToString());
            return ArgsString;
        }

        public static string GetFileString(string EnginePath)
        {
            return Path.Combine(EnginePath, "Engine", "Binaries", "Win64", "UE4Editor-Win64-DebugGame.exe");
        }
    }
}
