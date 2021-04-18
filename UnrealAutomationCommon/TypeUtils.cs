using System;
using System.Collections.Generic;
using System.Linq;

namespace UnrealAutomationCommon
{
    public static class TypeUtils
    {
        public static List<Type> GetSubclassesOf(Type superType, bool includeAbstract = false)
        {
            return (
                from domainAssembly in AppDomain.CurrentDomain.GetAssemblies()
                // alternative: from domainAssembly in domainAssembly.GetExportedTypes()
                from assemblyType in domainAssembly.GetTypes()
                // where superType.IsAssignableFrom(assemblyType)
                where assemblyType.IsSubclassOf(superType)
                && !assemblyType.IsAbstract || includeAbstract
                select assemblyType).ToList();
        }
    }
}
