using Newtonsoft.Json.Linq;
using UnrealAutomationCommon.Annotations;

namespace UnrealAutomationCommon
{
    static class JsonUtils
    {
        public static bool Set(this JObject jObject, string propertyName, [CanBeNull] JToken value)
        {
            if (!jObject.ContainsKey(propertyName))
            {
                // Add value
                jObject.Add(propertyName, value);
                return true;
            }

            // Update value
            if (jObject[propertyName] != value)
            {
                jObject[propertyName] = value;
                return true;
            }

            // Value already matches
            return false;
        }
    }
}
