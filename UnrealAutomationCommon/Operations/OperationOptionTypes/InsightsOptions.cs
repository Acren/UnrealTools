using System.ComponentModel;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class InsightsOptions : OperationOptions
    {
        private BindingList<TraceChannel> _traceChannels;

        public InsightsOptions()
        {
            TraceChannels = new BindingList<TraceChannel>();
            TraceChannels.RaiseListChangedEvents = true;
        }

        public override int Index => 30;

        public BindingList<TraceChannel> TraceChannels
        {
            get => _traceChannels;
            private set
            {
                if (_traceChannels != null)
                {
                    _traceChannels.ListChanged -= CollectionChanged;
                }

                _traceChannels = value;
                if (_traceChannels != null)
                {
                    _traceChannels.ListChanged += CollectionChanged;
                }

                void CollectionChanged(object sender, ListChangedEventArgs args)
                {
                    OnPropertyChanged();
                }
            }
        }
    }
}