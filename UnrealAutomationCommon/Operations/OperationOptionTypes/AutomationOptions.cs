namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class AutomationOptions : OperationOptions
    {
        private bool _runTests = false;

        public bool RunTests
        {
            get => _runTests;
            set
            {
                _runTests = value;
                OnPropertyChanged();
            }
        }
    }
}
