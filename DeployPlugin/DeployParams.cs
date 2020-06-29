namespace DeployPlugin
{
    public class DeployParams
    {
        public string PluginPath { get; set; }
        public bool Pak { get; set; }
        public bool RemoveSource { get; set; }
        public bool Upload { get; set; }
        public bool Archive { get; set; }

        public DeployParams()
        {
            PluginPath = null;
            Pak = false;
            RemoveSource = false;
            Upload = false;
            Archive = false;
        }

        public string PrintParameters()
        {
            return
                "Pak: " + Pak + "\n" +
                "Remove Source: " + RemoveSource + "\n" +
                "Upload: " + Upload + "\n" +
                "Archive: " + Archive;
        }
    }
}
