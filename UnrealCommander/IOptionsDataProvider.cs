using UnrealAutomationCommon.Operations;

namespace UnrealCommander
{
    public interface IOptionsDataProvider
    {
        public Operation Operation { get;}
        public OperationTarget OperationTarget { get;}
    }

}
