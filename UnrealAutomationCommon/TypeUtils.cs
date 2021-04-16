using System;
using System.Collections.Generic;
using System.Linq;

namespace UnrealAutomationCommon
{
    public static class TypeUtils
    {
        public static List<Type> GetSubclassesOf(Type SuperType)
        {
            return (
                from domainAssembly in AppDomain.CurrentDomain.GetAssemblies()
                // alternative: from domainAssembly in domainAssembly.GetExportedTypes()
                from assemblyType in domainAssembly.GetTypes()
                // where SuperType.IsAssignableFrom(assemblyType)
                where assemblyType.IsSubclassOf(SuperType)
                // alternative: && ! assemblyType.IsAbstract
                select assemblyType).ToList();
        }
    }
}
