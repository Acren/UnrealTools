using System.ComponentModel;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for InsightsOptionsControl.xaml
    /// </summary>
    public partial class InsightsOptionsControl : OptionsUserControl/*<InsightsOptions>*/
    {
        private InsightsOptions _options = null;

        public InsightsOptions Options
        {
            get => _options;
            set
            {
                _options = value;
                UpdateOptionsFromChannels();
                OnPropertyChanged();
            }
        }

        public InsightsOptionsControl()
        {
            InitializeComponent();

            foreach (TraceChannel channel in TraceChannels.Channels)
            {
                TraceChannelOptions.Add(new TraceChannelOption() { TraceChannel = channel, Enabled = false });
            }
            TraceChannelOptions.ListChanged += TraceChannelOptions_ListChanged;
        }

        private void TraceChannels_ListChanged(object sender, ListChangedEventArgs e)
        {
            UpdateOptionsFromChannels();
        }

        private void TraceChannelOptions_ListChanged(object sender, ListChangedEventArgs e)
        {
            Options.TraceChannels.Clear();

            foreach (TraceChannelOption option in TraceChannelOptions)
            {
                if (option.Enabled)
                {
                    Options.TraceChannels.Add(option.TraceChannel);
                }
            }
        }

        private void UpdateOptionsFromChannels()
        {
            TraceChannelOptions.RaiseListChangedEvents = false;

            foreach (TraceChannelOption option in TraceChannelOptions)
            {
                option.Enabled = Options.TraceChannels.Contains(option.TraceChannel);
            }

            TraceChannelOptions.RaiseListChangedEvents = true;
        }

        public BindingList<TraceChannelOption> TraceChannelOptions { get; set; } = new BindingList<TraceChannelOption>();
    }
}
