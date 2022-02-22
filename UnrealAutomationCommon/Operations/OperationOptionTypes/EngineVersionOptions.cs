using System.ComponentModel;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class EngineVersionOptions : OperationOptions
    {
        private BindingList<EngineInstall> _engineInstalls = new();

        public EngineVersionOptions()
        {

        }

        public override int Index => 10;


    }
}