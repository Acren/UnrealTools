using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public class OperationParameters : INotifyPropertyChanged
    {
        private string _additionalArguments;

        private IOperationTarget _target;

        private BindingList<OperationOptions> _optionsInstances;

        public OperationParameters()
        {
            OptionsInstances = new BindingList<OperationOptions>();
        }

        [JsonIgnore]
        public string OutputPathOverride { get; set; }

        [JsonIgnore]
        public EngineInstall EngineOverride { get; set; }

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public BindingList<OperationOptions> OptionsInstances
        {
            get => _optionsInstances;
            set
            {
                // Sort initial options - note this creates a new BindingList instance
                List<OperationOptions> initialOptions = value.ToList();
                initialOptions.Sort();
                _optionsInstances = new BindingList<OperationOptions>(initialOptions);

                OptionsInstances.ListChanged += (sender, args) => OnPropertyChanged(nameof(OptionsInstances));

                UpdateOptionsTarget();
            }
        }

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

                    // Notify options about new target
                    UpdateOptionsTarget();

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
            SetOptions(newOptions);
            return (T)newOptions.Clone();
        }

        public void SetOptions<T>(T options) where T : OperationOptions
        {
            if (FindOptions<T>() != null)
            {
                throw new Exception("Parameters already has options of this type");
            }

            // Find index to insert at
            int desiredIndex = 0;
            foreach (OperationOptions optionsInstance in OptionsInstances)
            {
                if (options.CompareTo(optionsInstance) > 0)
                {
                    break;
                }

                desiredIndex++;
            }

            options.OperationTarget = Target;

            OptionsInstances.Insert(desiredIndex, options);
        }

        public void ResetOptions()
        {
            OptionsInstances.Clear();
        }

        private void UpdateOptionsTarget()
        {
            foreach (OperationOptions options in OptionsInstances)
            {
                options.OperationTarget = Target;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}