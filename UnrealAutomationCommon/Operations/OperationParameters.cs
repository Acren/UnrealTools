using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public class OperationParameters : INotifyPropertyChanged
    {
        private OperationTarget _target;

        private BindingList<OperationOptions> _optionsInstances = new BindingList<OperationOptions>();

        private string _additionalArguments;

        public event PropertyChangedEventHandler PropertyChanged;

        public OperationParameters()
        {
            _optionsInstances.ListChanged += _optionsInstances_ListChanged;
        }

        private void _optionsInstances_ListChanged(object sender, ListChangedEventArgs e)
        {
            OnPropertyChanged(nameof(OptionsInstances));
        }

        public BindingList<OperationOptions> OptionsInstances => _optionsInstances;

        public OperationTarget Target
        {
            get => _target;
            set
            {
                if (_target != value)
                {
                    if (_target != null)
                    {
                        _target.PropertyChanged -= TargetChanged;
                    }
                    _target = value;
                    if (_target != null)
                    {
                        _target.PropertyChanged += TargetChanged;
                    }
                    OnPropertyChanged();
                }
                void TargetChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                }
            }
        }

        public string AdditionalArguments
        {
            get => _additionalArguments;
            set
            {
                _additionalArguments = value;
                OnPropertyChanged();
            }
        }

        public T FindOptions<T>() where T : OperationOptions
        {
            foreach (OperationOptions options in _optionsInstances)
            {
                if (options.GetType() == typeof(T))
                {
                    return (T)options;
                }
            }

            return null;
        }

        public T RequestOptions<T>() where T : OperationOptions
        {
            T options = FindOptions<T>();

            if (options != null)
            {
                return options;
            }

            options = (T)Activator.CreateInstance(typeof(T));
            _optionsInstances.Add(options);
            return options;
        }

        public void ResetOptions()
        {
            _optionsInstances.Clear();
        }

        [JsonIgnore]
        public string OutputPathRoot => @"C:\UnrealCommander\";
        [JsonIgnore]
        public bool UseOutputPathProjectSubfolder => true;
        [JsonIgnore]
        public bool UseOutputPathOperationSubfolder => true;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
