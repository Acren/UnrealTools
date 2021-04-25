using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

namespace UnrealAutomationCommon
{
    public enum TestState
    {
        Fail,
        Success
    }

    public class Test
    {
        public string TestDisplayName { get; set; }
        public string FullTestPath { get; set; }
        public TestState State { get; set; }
    }

    public class TestReport
    {
        public List<Test> Tests { get; set; }

        public static TestReport Load(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }
            return JsonConvert.DeserializeObject<TestReport>(File.ReadAllText(filePath));
        }
    }
}
