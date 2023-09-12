using System;
using System.Collections.Generic;
using System.Text;
using Semver;

namespace UnrealAutomationCommon
{
    public static class VersionUtils
    {
        public static int ToInt(this SemVersion version)
        {
            if (version.Minor > 99 || version.Patch > 99)
            {
                throw new Exception("Version exceeds range of integer conversion");
            }
            // Pad each component to 2 digits
            // 1.2.3 -> 10203
            return version.Major * 10000 + version.Minor * 100 + version.Patch;
        }
    }
}
