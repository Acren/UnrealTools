using System;
using System.IO;
using System.Linq;
using System.Xml;
using UnrealAutomationCommon;

namespace ParseTestReport
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("ParseTestReport for parsing Unreal test results");

            if (args.Length < 1)
            {
                Console.WriteLine("No arguments - first argument must be path of test report");
                Environment.Exit(2);
            }

            string path = args[0];
            if (!File.Exists(path))
            {
                Console.WriteLine("File " + path + " does not exist");
                Environment.Exit(3);
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
                        Console.WriteLine("".PadRight(9) + " - " + entry.Event.Message);
                    }
                }
            }

            if (args.Contains("-junit"))
            {
                bool noWarnings = args.Contains("-nowarnings");

                string directory = Path.GetDirectoryName(path);
                string jUnitPath = Path.Combine(directory, "junit.xml");
                XmlDocument jUnit = report.ToJUnit(!noWarnings);

                XmlWriterSettings settings = new XmlWriterSettings();
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
