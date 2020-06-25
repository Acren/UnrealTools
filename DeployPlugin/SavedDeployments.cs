using System.Collections.Generic;

namespace DeployPlugin
{
    [System.Configuration.SettingsSerializeAsAttribute(System.Configuration.SettingsSerializeAs.Xml)]
    public class SavedDeployments
    {
        public List<DeployParams> PluginParams { get; set;}

        public SavedDeployments()
        {
            PluginParams = new List<DeployParams>();
        }
    }
}
