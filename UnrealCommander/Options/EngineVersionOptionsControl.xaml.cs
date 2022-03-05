using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander.Options
{
    public class EngineVersionOption : INotifyPropertyChanged
    {
        private bool _enabled;
        private EngineInstallVersion _engineVersion;

        public EngineInstallVersion EngineVersion
        {
            get => _engineVersion;
            set
            {
                _engineVersion = value;
                OnPropertyChanged();
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    ///     Interaction logic for EngineVersionOptionsControl.xaml
    /// </summary>
    public partial class EngineVersionOptionsControl : OptionsUserControl
    {
        public EngineVersionOptionsControl()
        {
            DataContextChanged += (sender, args) =>
            {
                if (args.OldValue is OperationOptions oldOptions)
                {
                    oldOptions.PropertyChanged -= OptionsPropertyChanged;
                }

                Options.PropertyChanged += OptionsPropertyChanged;

                void OptionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
                {
                    UpdateOptionsEnabled();
                }

                UpdateOptionsEnabled();
            };

            InitializeComponent();

            // todo construct dynamically
            EngineVersionOptions.Add(new EngineVersionOption() { Enabled = false, EngineVersion = new EngineInstallVersion(4, 23) });
            EngineVersionOptions.Add(new EngineVersionOption() { Enabled = false, EngineVersion = new EngineInstallVersion(4, 24) });
            EngineVersionOptions.Add(new EngineVersionOption() { Enabled = false, EngineVersion = new EngineInstallVersion(4, 25) });
            EngineVersionOptions.Add(new EngineVersionOption() { Enabled = false, EngineVersion = new EngineInstallVersion(4, 26) });
            EngineVersionOptions.Add(new EngineVersionOption() { Enabled = false, EngineVersion = new EngineInstallVersion(4, 27) });
            EngineVersionOptions.Add(new EngineVersionOption() { Enabled = false, EngineVersion = new EngineInstallVersion(5, 0) });

            EngineVersionOptions.ListChanged += (sender, args) =>
            {
                List<EngineInstallVersion> enabledVersions = new();
                foreach (EngineVersionOption version in EngineVersionOptions)
                {
                    if (version.Enabled)
                    {
                        enabledVersions.Add(version.EngineVersion);
                    }
                }

                (Options as EngineVersionOptions).EnabledVersions.Value = enabledVersions;

                //if ((DataContext as EngineVersionOptions)?.OperationTarget is Plugin plugin)
                //{
                //    plugin.TargetEngineVersions = enabledVersions;
                //}
            };

            //UpdateOptionsEnabled();
        }

        public override void EndInit()
        {
            UpdateOptionsEnabled();
            base.EndInit();
        }

        public BindingList<EngineVersionOption> EngineVersionOptions { get; set; } = new();

        private void UpdateOptionsEnabled()
        {
            EngineVersionOptions.RaiseListChangedEvents = false;
            foreach (EngineVersionOption version in EngineVersionOptions)
            {
                version.Enabled = (Options as EngineVersionOptions).EnabledVersions.Value.Contains(version.EngineVersion);
            }
            EngineVersionOptions.RaiseListChangedEvents = true;
        }
    }
}