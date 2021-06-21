using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

namespace UnrealAutomationCommon
{
    public enum TestEventType
    {
        Info,
        Warning,
        Error
    }

    public enum TestState
    {
        Fail,
        Success
    }

    public class TestEvent
    {
        public TestEventType Type { get; set; }
        public string Message { get; set; }
    }

    public class TestEntry
    {
        public TestEvent Event { get; set; }
    }

    public class Test
    {
        public string TestDisplayName { get; set; }
        public string FullTestPath { get; set; }
        public TestState State { get; set; }
        public List<TestEntry> Entries { get; set; }
    }

    public class TestReport
    {
        public List<Test> Tests { get; set; }
        public Int32 Failed { get; set; }

        public static TestReport Load(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }
            return JsonConvert.DeserializeObject<TestReport>(File.ReadAllText(filePath));
        }

        public TestState GetState()
        {
            if (Failed > 0)
            {
                return TestState.Fail;
            }

            return TestState.Success;
        }
    }
}
