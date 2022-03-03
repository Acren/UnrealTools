using System.ComponentModel;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander.Options
{
    /// <summary>
    ///     Interaction logic for InsightsOptionsControl.xaml
    /// </summary>
    public partial class InsightsOptionsControl : OptionsUserControl /*<InsightsOptions>*/
    {
        public InsightsOptionsControl()
        {
            InitializeComponent();

            foreach (TraceChannel channel in TraceChannels.Channels)
            {
                TraceChannelOptions.Add(new TraceChannelOption { TraceChannel = channel, Enabled = false });
            }

            TraceChannelOptions.ListChanged += TraceChannelOptions_ListChanged;
        }

        private InsightsOptions InsightsOptions => DataContext as InsightsOptions;

        public BindingList<TraceChannelOption> TraceChannelOptions { get; set; } = new();

        public override void EndInit()
        {
            UpdateOptionsFromChannels();
            base.EndInit();
        }

        private void TraceChannels_ListChanged(object sender, ListChangedEventArgs e)
        {
            UpdateOptionsFromChannels();
        }

        private void TraceChannelOptions_ListChanged(object sender, ListChangedEventArgs e)
        {
            InsightsOptions.TraceChannels.Clear();

            foreach (TraceChannelOption option in TraceChannelOptions)
            {
                if (option.Enabled)
                {
                    InsightsOptions.TraceChannels.Add(option.TraceChannel);
                }
            }
        }

        private void UpdateOptionsFromChannels()
        {
            TraceChannelOptions.RaiseListChangedEvents = false;

            foreach (TraceChannelOption option in TraceChannelOptions) option.Enabled = InsightsOptions.TraceChannels.Contains(option.TraceChannel);

            TraceChannelOptions.RaiseListChangedEvents = true;
        }
    }
}