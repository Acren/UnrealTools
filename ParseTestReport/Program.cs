using System;
using System.IO;
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

            if (report.GetState() == TestState.Fail)
            {
                Console.WriteLine(report.Failed + " tests failed");
                Environment.Exit(1);
            }

            Console.WriteLine("All tests passed");
            Environment.Exit(0);
        }
    }
}
