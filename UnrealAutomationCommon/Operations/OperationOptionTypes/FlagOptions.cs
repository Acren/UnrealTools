namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class FlagOptions : OperationOptions
    {
        private bool _stompMalloc;
        private bool _waitForAttach;

        public override int Index => 40;
        public override string Name => "Flags";

        public bool StompMalloc
        {
            get => _stompMalloc;
            set
            {
                _stompMalloc = value;
                OnPropertyChanged();
            }
        }

        public bool WaitForAttach
        {
            get => _waitForAttach;
            set
            {
                _waitForAttach = value;
                OnPropertyChanged();
            }
        }
    }
}