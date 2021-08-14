﻿using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    public interface IEngineInstallProvider
    {
        [JsonIgnore]
        public EngineInstall EngineInstall { get; }
    }
}