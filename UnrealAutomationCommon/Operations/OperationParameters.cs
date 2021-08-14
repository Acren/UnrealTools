using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UnrealAutomationCommon.Operations
{
    public class OperationParameters : INotifyPropertyChanged
    {
        private IOperationTarget _target;

        private BindingList<OperationOptions> _optionsInstances = new BindingList<OperationOptions>();

        private string _additionalArguments;

        [JsonIgnore]
        public string OutputPathOverride { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public OperationParameters()
        {
            _optionsInstances.ListChanged += ((sender, args) => OnPropertyChanged(nameof(OptionsInstances)));
        }

        public BindingList<OperationOptions> OptionsInstances => _optionsInstances;

        public IOperationTarget Target
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

        public void SetOptions<T>(T options) where T : OperationOptions
        {
            if (FindOptions<T>() != null)
            {
                throw new Exception("Parameters already has options of this type");
            }
            _optionsInstances.Add(options);
        }

        public void ResetOptions()
        {
            _optionsInstances.Clear();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
