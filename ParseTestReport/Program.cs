using System;
using System.IO;
using System.Linq;
using System.Xml;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Unreal;

namespace ParseTestReport
{
    internal class Program
    {
        private static void Main(string[] argStrings)
        {
            Console.WriteLine("ParseTestReport for parsing Unreal test results");

            Arguments args = new Arguments(argStrings);

            if (argStrings.Length < 1)
            {
                Console.WriteLine("No arguments - first argument must be path of test report");
                Environment.Exit(2);
            }

            string path = argStrings[0];
            if (!File.Exists(path))
            {
                Console.WriteLine("File " + path + " does not exist");
                Environment.Exit(3);
            }

            string context = null;
            if(args.HasArgument("context"))
            {
                context = args.GetArgument("context").Value;
                Console.WriteLine($"Context is '{context}'");
            }
            else
            {
                Console.WriteLine($"No context label provided");
            }

            TestReport report = null;

            try
            {
                report = TestReport.Load(path);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Console.WriteLine("Failed to load test report");
                Environment.Exit(4);
            }

            Console.WriteLine(report.TotalSucceeded + " of " + report.Tests.Count + " tests passed");
            foreach (Test test in report.Tests)
            {
                Console.WriteLine(EnumUtils.GetName(test.State).ToUpperInvariant().PadRight(7) + " - " + test.FullTestPath);
                foreach (TestEntry entry in test.Entries)
                {
                    if (entry.Event.Type != TestEventType.Info)
                    {
                        string typeString = entry.Event.Type == TestEventType.Error ? "[ERROR]" : "[WARNING]";
                        Console.WriteLine($"{string.Empty,-11} - {typeString,-9} - {entry.Event.Message}");
                    }
                }
            }

            if (args.HasArgument("junit"))
            {
                bool noWarnings = args.HasArgument("nowarnings");

                string directory = Path.GetDirectoryName(path);
                string jUnitPath = Path.Combine(directory, "junit.xml");
                XmlDocument jUnit = report.ToJUnit(!noWarnings, context);

                XmlWriterSettings settings = new();
                settings.Indent = true;
                XmlWriter writer = XmlWriter.Create(jUnitPath, settings);

                jUnit.WriteTo(writer);

                writer.Close();

                Console.WriteLine("Created JUnit report at " + jUnitPath);
            }

            if (report.GetState() == TestState.Fail)
            {
                Environment.Exit(1);
            }

            Environment.Exit(0);
        }
    }
}