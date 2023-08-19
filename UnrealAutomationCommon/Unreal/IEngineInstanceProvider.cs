using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    public interface IEngineInstanceProvider
    {
        [JsonIgnore] public Engine EngineInstance { get; }

        [JsonIgnore] public string EngineInstanceName => EngineInstance.Name;
    }
}