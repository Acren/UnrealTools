using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace UnrealAutomationCommon.Unreal
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
        public int Succeeded { get; set; }
        public int SucceededWithWarnings { get; set; }
        public int Failed { get; set; }
        public float TotalDuration { get; set; }

        public int TotalSucceeded => Succeeded + SucceededWithWarnings;
        public int TotalNumTests => Tests.Count;

        public static TestReport Load(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            return JsonConvert.DeserializeObject<TestReport>(File.ReadAllText(filePath));
        }

        public TestState GetState()
        {
            if (Failed > 0) return TestState.Fail;

            return TestState.Success;
        }

        public XmlDocument ToJUnit(bool includeWarnings)
        {
            XmlDocument doc = new();
            XmlElement testSuites = doc.CreateElement("testsuites");
            doc.AppendChild(testSuites);
            testSuites.SetAttribute("duration", TotalDuration.ToString());
            testSuites.SetAttribute("tests", TotalNumTests.ToString());
            testSuites.SetAttribute("failures", Failed.ToString());
            XmlElement testSuite = doc.CreateElement("testsuite");
            testSuites.AppendChild(testSuite);
            testSuite.SetAttribute("tests", TotalNumTests.ToString());
            testSuite.SetAttribute("failures", Failed.ToString());
            foreach (Test test in Tests)
            {
                XmlElement testCase = doc.CreateElement("testcase");
                testSuite.AppendChild(testCase);
                testCase.SetAttribute("classname", test.FullTestPath);
                testCase.SetAttribute("name", test.TestDisplayName);

                TestEventType mostSevere = TestEventType.Info;
                var failureLines = new List<string>();
                foreach (TestEntry testEntry in test.Entries)
                {
                    bool includeEvent = testEntry.Event.Type == TestEventType.Error ||
                                        testEntry.Event.Type == TestEventType.Warning && includeWarnings;
                    if (includeEvent)
                    {
                        failureLines.Add(EnumUtils.GetName(testEntry.Event.Type) + ": " + testEntry.Event.Message);
                        if (testEntry.Event.Type > mostSevere) mostSevere = testEntry.Event.Type;
                    }
                }

                if (mostSevere != TestEventType.Info)
                {
                    XmlElement failure = doc.CreateElement("failure");
                    testCase.AppendChild(failure);
                    failure.SetAttribute("type", EnumUtils.GetName(mostSevere));
                    failure.SetAttribute("message", string.Join(Environment.NewLine, failureLines));
                }
            }

            return doc;
        }
    }
}