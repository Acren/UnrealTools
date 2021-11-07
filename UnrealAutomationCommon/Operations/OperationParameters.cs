using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UnrealAutomationCommon.Operations
{
    public class OperationParameters : INotifyPropertyChanged
    {
        private string _additionalArguments;

        private IOperationTarget _target;

        public OperationParameters()
        {
            OptionsInstances.ListChanged += (sender, args) => OnPropertyChanged(nameof(OptionsInstances));
        }

        [JsonIgnore] public string OutputPathOverride { get; set; }

        public BindingList<OperationOptions> OptionsInstances { get; } = new();

        public IOperationTarget Target
        {
            get => _target;
            set
            {
                if (_target != value)
                {
                    if (_target != null) _target.PropertyChanged -= TargetChanged;
                    _target = value;
                    if (_target != null) _target.PropertyChanged += TargetChanged;
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

        public event PropertyChangedEventHandler PropertyChanged;

        public T FindOptions<T>() where T : OperationOptions
        {
            foreach (OperationOptions options in OptionsInstances)
            {
                if (options.GetType() == typeof(T))
                {
                    return (T)options.Clone();
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

            T newOptions = (T)Activator.CreateInstance(typeof(T));
            OptionsInstances.Add(newOptions);
            return (T)newOptions.Clone();
        }

        public void SetOptions<T>(T options) where T : OperationOptions
        {
            if (FindOptions<T>() != null) throw new Exception("Parameters already has options of this type");
            OptionsInstances.Add(options);
        }

        public void ResetOptions()
        {
            OptionsInstances.Clear();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}