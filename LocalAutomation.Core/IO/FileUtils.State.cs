using System;
using System.IO;
using System.Threading;

namespace LocalAutomation.Core.IO;

public static partial class FileUtils
{
    /// <summary>
    /// Blocks until one file can be opened for reading or times out.
    /// </summary>
    public static void WaitForFileReadable(string filePath)
    {
        int secondsWaited = 0;
        while (true)
        {
            try
            {
                using StreamReader stream = new(filePath);
                return;
            }
            catch
            {
                Thread.Sleep(1000);
                secondsWaited++;
            }

            if (secondsWaited >= 10)
            {
                throw new Exception("Timed out");
            }
        }
    }

    /// <summary>
    /// Checks whether one existing file has the specified extension.
    /// </summary>
    public static bool HasExtension(string filePath, string extension)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        if (!Path.HasExtension(filePath))
        {
            return false;
        }

        return Path.GetExtension(filePath).Equals(extension, StringComparison.InvariantCultureIgnoreCase);
    }
}
