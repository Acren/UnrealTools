using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace MarketplaceDeployConsole
{
    public class PluginDefinition
    {
        public string VersionName { get; set; }
        public string FriendlyName { get; set; }
        public string EngineVersion { get; set; }
    }

    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool Deploy = true;

            while (Deploy)
            {
                PromptParametersAndDeploy();

                FlashWindow.Flash(Process.GetCurrentProcess().MainWindowHandle);

                Console.WriteLine("Deploy again? y/[n]");
                ConsoleKey DeplopyKey = Console.ReadKey(true).Key;
                Deploy = DeplopyKey == ConsoleKey.Y;
            }

        }

        static void PromptParametersAndDeploy()
        {
            try
            {
                ConsoleUtils.WriteHeader("MarketplaceDeployConsole started");
                Console.WriteLine("Select a plugin to deploy");

                SavedDeployments SavedDeployments = Properties.Settings.Default.SavedDeployments ?? new SavedDeployments();

                // Clear out invalid paths
                SavedDeployments.PluginParams.RemoveAll(Params => !Directory.Exists(Params.PluginPath));

                Properties.Settings.Default.SavedDeployments = SavedDeployments;
                Properties.Settings.Default.Save();

                // Build menu

                EasyConsole.Menu Menu = new EasyConsole.Menu();

                foreach (DeployParams Params in SavedDeployments.PluginParams)
                {
                    string PluginName = DeployUtils.FindPluginName(Params.PluginPath);
                    string ProjectName = DeployUtils.FindHostProjectName(Params.PluginPath);
                    Menu.Add(ProjectName + "/" + PluginName, () =>
                    {
                        // Selected existing plugin
                        DeployParams UseParams = Params;

                        ConsoleUtils.WriteHeader("Deploying " + DeployUtils.FindPluginName(Params.PluginPath));

                        Console.WriteLine("Last used parameters were");
                        Console.WriteLine();
                        Console.WriteLine(UseParams.PrintParameters());
                        Console.WriteLine();

                        bool Reuse = ConsoleUtils.PromptBool("Use these again?", true);

                        if(!Reuse)
                        {
                            UseParams = PromptParams(UseParams);
                        }

                        SaveParams(Params);
                        Deploy.RunDeployment(Params);

                    });
                }

                Menu.Add("New", () =>
                {
                    string SelectDirectoryPrompt = "Select plugin directory within its example/host project";
                    Console.WriteLine(SelectDirectoryPrompt);
                    FolderBrowserDialog Dialog = new FolderBrowserDialog();
                    Dialog.Description = SelectDirectoryPrompt;
                    Dialog.SelectedPath = Properties.Settings.Default.PluginPath;

                    if (Dialog.ShowDialog() == DialogResult.OK)
                    {
                        DeployParams Params = new DeployParams();
                        Params.PluginPath = Dialog.SelectedPath;

                        ConsoleUtils.WriteHeader("Deploying " + DeployUtils.FindPluginName(Params.PluginPath));

                        Params = PromptParams(Params);

                        SaveParams(Params);
                        Deploy.RunDeployment(Params);
                    }
                });

                Menu.Display();
            }
            catch (Exception Ex)
            {
                ConsoleUtils.WriteHeader("Encountered exception");

                Console.WriteLine(Ex.Message);
                Console.WriteLine(Ex.StackTrace);
                Console.WriteLine(Ex.ToString());
            }
        }

        static DeployParams PromptParams(DeployParams Params)
        {
            Params.Pak = ConsoleUtils.PromptBool("Pak content?", true, "Will pak content", "Won't pak content");
            Params.RemoveSource = ConsoleUtils.PromptBool("Remove example project source when building Demo Exe?", true, "Will remove source for Demo Exe", "Won't remove source for Demo Exe");
            Params.Upload = ConsoleUtils.PromptBool("Upload to Drive?", true, "Will archive and upload", "Won't upload");

            if (Params.Upload)
            {
                Params.Archive = true;
            }
            else
            {
                Params.Archive = ConsoleUtils.PromptBool("Archive?", false, "Will archive", "Won't archive");
            }

            return Params;
        }

        static void SaveParams(DeployParams NewParams)
        {
            SavedDeployments SavedDeployments = Properties.Settings.Default.SavedDeployments;

            bool Updated = false;
            for (int i = 0; i < SavedDeployments.PluginParams.Count; i++)
            {
                if(SavedDeployments.PluginParams[i].PluginPath == NewParams.PluginPath)
                {
                    SavedDeployments.PluginParams[i] = NewParams;
                    Updated = true;
                    break;
                }
            }

            if(!Updated)
            {
                SavedDeployments.PluginParams.Add(NewParams);
            }

            Properties.Settings.Default.Save();
        }


    }
}
