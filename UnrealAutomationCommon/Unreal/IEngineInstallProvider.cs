using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    public interface IEngineInstallProvider
    {
        [JsonIgnore] public EngineInstall EngineInstallInstance { get; }

        [JsonIgnore] public string EngineInstallName { get; }
    }
}