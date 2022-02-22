using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using UnrealAutomationCommon.Annotations;

namespace UnrealAutomationCommon.Operations
{
    public class Option : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class Option<T> : Option
    {
        private T _value;

        public Option(T defaultValue)
        {
            _value = defaultValue;
        }
        public Option(Action changedCallback, T defaultValue)
        {
            _value = defaultValue;
            PropertyChanged += (sender, args) => changedCallback();
        }

        public T Value
        {
            get => _value;
            set
            {
                _value = value;
                OnPropertyChanged();
            }
        }

        public static implicit operator T(Option<T> option)
        {
            return option.Value;
        }

        public static implicit operator Option<T>(T value)
        {
            return new Option<T>(value);
        }
    }

    public class OperationOptions : INotifyPropertyChanged, IComparable<OperationOptions>
    {
        private IOperationTarget _operationTarget = null;

        public OperationOptions()
        {
            PropertyInfo[] properties = GetType().GetProperties();
            foreach (PropertyInfo property in properties)
            {
                if (property.PropertyType.IsSubclassOf(typeof(Option)))
                {
                    if (property.GetValue(this) is Option option)
                    {
                        option.PropertyChanged += (sender, args) => OptionChanged();
                    }
                }
            }
        }

        // Options index for ordering
        [JsonIgnore]
        public virtual int Index => 0;

        [JsonIgnore]
        public virtual string Name
        {
            get
            {
                string name = GetType().Name;
                name = name.Replace("Options", "");
                return name.SplitWordsByUppercase();
            }
        }

        [JsonIgnore]
        public IOperationTarget OperationTarget
        {
            get => _operationTarget;
            set
            {
                _operationTarget = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public OperationOptions Clone()
        {
            return (OperationOptions)MemberwiseClone();
        }

        protected void OptionChanged()
        {
            OnPropertyChanged();
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public int CompareTo(OperationOptions other)
        {
            // First try compare by index
            if (Index != other.Index)
            {
                return Index.CompareTo(other.Index);
            }

            // Then by name
            return String.Compare(Name, other.Name, StringComparison.Ordinal);
        }
    }
}