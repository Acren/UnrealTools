using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public delegate void RetryHandler(Exception ex);

    public class OperationParameters : INotifyPropertyChanged
    {
        private string _additionalArguments;

        private IOperationTarget _target;

        private BindingList<OperationOptions> _optionsInstances;

        // Bubble nested option changes up to the parameters object so the UI refreshes when a checkbox changes.
        private void HandleOptionsInstancePropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            OnPropertyChanged(nameof(OptionsInstances));
            OnPropertyChanged(nameof(Engine));
        }

        // Rebind option change listeners whenever the option set list changes or is replaced.
        private void RefreshOptionsSubscriptions()
        {
            if (_optionsInstances == null)
            {
                return;
            }

            foreach (OperationOptions options in _optionsInstances)
            {
                options.PropertyChanged -= HandleOptionsInstancePropertyChanged;
                options.PropertyChanged += HandleOptionsInstancePropertyChanged;
            }
        }

        public OperationParameters()
        {
            OptionsInstances = new BindingList<OperationOptions>();
        }

        // Delegate used to determine if failed action should be retried or cancelled
        // Useful for triggering "retry?" message boxes
        [JsonIgnore]
        public RetryHandler RetryHandler { get; set; }

        [JsonIgnore]
        public string OutputPathOverride { get; set; }

        [JsonIgnore]
        public Engine EngineOverride { get; set; }

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

                OptionsInstances.ListChanged += (sender, args) =>
                {
                    RefreshOptionsSubscriptions();
                    OnPropertyChanged(nameof(OptionsInstances));
                    OnPropertyChanged(nameof(Engine));
                };

                RefreshOptionsSubscriptions();
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

        // Treat the active engine as derived runtime state only. Serializing this getter causes Json.NET to invoke
        // RequestOptions<EngineVersionOptions>(), which can mutate option collections during persistence and recurse
        // back into save handlers.
        [JsonIgnore]
        public Engine Engine
        {
            get
            {
                // Return engine install override if there is one
                if (EngineOverride != null)
                {
                    return EngineOverride;
                }

                var VersionOptions = RequestOptions<EngineVersionOptions>();
                if (VersionOptions != null && VersionOptions.EnabledVersions.Value.Count > 0)
                {
                    EngineVersion version = VersionOptions.EnabledVersions.Value[0];
                    if (version != null)
                    {
                        return EngineFinder.GetEngineInstall(version);
                    }
                }

                if (Target is not IEngineInstanceProvider)
                {
                    return null;
                }

                IEngineInstanceProvider engineInstanceProvider = Target as IEngineInstanceProvider;
                return engineInstanceProvider?.EngineInstance;
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

        // Return the live options instance stored on this parameter object so new UI layers can bind directly to the
        // actual state instead of working with the historical clone-based helpers above.
        public OperationOptions GetOptionsInstance(Type optionsType)
        {
            foreach (OperationOptions options in OptionsInstances)
            {
                if (options.GetType() == optionsType)
                {
                    return options;
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

        // Ensure a live options instance exists for the provided runtime type and return the stored instance so host
        // UIs can edit it directly.
        public OperationOptions EnsureOptionsInstance(Type optionsType)
        {
            OperationOptions existingOptions = GetOptionsInstance(optionsType);
            if (existingOptions != null)
            {
                return existingOptions;
            }

            OperationOptions newOptions = (OperationOptions)Activator.CreateInstance(optionsType);
            SetOptions(newOptions);
            return newOptions;
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
                if (options.CompareTo(optionsInstance) < 0)
                {
                    break;
                }

                desiredIndex++;
            }

            options.OperationTarget = Target;

            OptionsInstances.Insert(desiredIndex, options);
        }

        // Remove the live options instance for the provided runtime type when a target or operation no longer needs
        // that option set.
        public bool RemoveOptionsInstance(Type optionsType)
        {
            OperationOptions options = GetOptionsInstance(optionsType);
            if (options == null)
            {
                return false;
            }

            return OptionsInstances.Remove(options);
        }

        public void ResetOptions()
        {
            OptionsInstances.Clear();
        }

        private void UpdateOptionsTarget()
        {
            foreach (OperationOptions options in OptionsInstances.ToList())
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
