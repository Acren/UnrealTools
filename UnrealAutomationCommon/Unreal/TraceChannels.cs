using System;
using System.Collections.Generic;

namespace UnrealAutomationCommon.Unreal
{
    public class TraceChannel : IEquatable<TraceChannel>
    {
        public string Key { get; set; }
        public string Label { get; set; }

        public bool Equals(TraceChannel other)
        {
            return Key == other.Key;
        }
    }

    public static class TraceChannels
    {
        public static readonly List<TraceChannel> Channels = new List<TraceChannel>()
        {
            new TraceChannel(){Key= "log", Label = "Log"},
            new TraceChannel(){Key= "counters", Label = "Counters"},
            new TraceChannel(){Key= "cpu", Label = "CPU"},
            new TraceChannel(){Key= "frame", Label = "Frame"},
            new TraceChannel(){Key= "bookmark", Label = "Bookmark"},
            new TraceChannel(){Key= "file", Label = "File"},
            new TraceChannel(){Key= "loadtime", Label = "Load Time"},
            new TraceChannel(){Key= "gpu", Label = "GPU"},
            new TraceChannel(){Key= "rhicommands", Label = "RHI Commands"},
            new TraceChannel(){Key= "rendercommands", Label = "Render Commands"},
            new TraceChannel(){Key= "object", Label = "Object"}
        };

    }
}
