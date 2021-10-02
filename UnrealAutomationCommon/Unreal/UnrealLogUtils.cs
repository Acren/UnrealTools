namespace UnrealAutomationCommon.Unreal
{
    public static class UnrealLogUtils
    {
        // Hacky way of checking log follows syntax:
        // [*][int]*
        // If it does, we assume it's a timestamped line
        public static bool IsTimestampedLog(string line)
        {
            if (line.StartsWith("["))
            {
                string[] split = line.Split('[');
                if (split.Length >= 2 && split[1].EndsWith("]") && split[2].Contains("]"))
                {
                    string[] closeSplit = split[2].Split(']');
                    int Result;
                    if (int.TryParse(closeSplit[0], out Result))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Assumes the line actually has a timestamp, otherwise might remove non-timestamp parts
        public static string RemoveTimestamp(string line)
        {
            int secondCloseIndex = line.IndexOf(']', line.IndexOf(']') + 1);
            return line.Remove(0, secondCloseIndex + 1);
        }
    }
}
