using System;

namespace DeployPlugin
{
    class ConsoleUtils
    {
        public static void RetryLoop(Action TargetAction, string FailureMessage, bool OutputException)
        {
            while (true)
            {
                try
                {
                    TargetAction.Invoke();
                    break;
                }
                catch (Exception Ex)
                {
                    Console.WriteLine(FailureMessage);
                    if (OutputException)
                    {
                        Console.WriteLine(Ex.Message);
                    }
                    Console.WriteLine("Retry?");
                    if (Console.ReadKey(true).Key == ConsoleKey.Y)
                    {
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        public static bool PromptBool(string PromptString, bool Default, string YesConfirm = null, string NoConfirm = null)
        {
            string DefaultString = Default ? "[y]/n" : "y/[n]";
            Console.WriteLine(PromptString + " " + DefaultString);
            ConsoleKey Key = Console.ReadKey(true).Key;
            bool KeyValue = Key == ConsoleKey.Y;
            if (!KeyValue && Default)
            {
                KeyValue = Key == ConsoleKey.Enter;
            }

            if (KeyValue)
            {
                if (YesConfirm != null)
                {
                    Console.WriteLine(YesConfirm);
                }
            }
            else
            {
                if (NoConfirm != null)
                {
                    Console.WriteLine(NoConfirm);
                }
            }
            return KeyValue;
        }

        public static void WriteHeader(string HeaderString)
        {
            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine(HeaderString);
            Console.WriteLine("--------------------------------------------------------------------------------");
        }
    }
}
