using System.Windows;
using LocalAutomation.Extensions.Unreal;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace UnrealCommander
{
    /// <summary>
    ///     Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Exposes the shared LocalAutomation application services so the legacy WPF shell can consume the same
        /// extension-backed discovery model as the new Avalonia shell.
        /// </summary>
        public static LocalAutomationApplicationHost Services { get; } = CreateApplicationHost();

        protected override void OnStartup(StartupEventArgs e)
        {
            Initialize();
        }

        private void Initialize()
        {

        }

        /// <summary>
        /// Registers the compile-time modules used by the legacy WPF shell.
        /// </summary>
        private static LocalAutomationApplicationHost CreateApplicationHost()
        {
            return LocalAutomationApplicationHost.Create(new UnrealExtensionModule());
        }
    }
}
