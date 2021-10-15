using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Annotations;

namespace UnrealAutomationCommon.Operations
{
    public class Option<T> : INotifyPropertyChanged
    {
        private T _value;

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

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public static implicit operator T(Option<T> option)
        {
            return option.Value;
        }
    }

    public class OperationOptions : INotifyPropertyChanged
    {
        protected Option<T> AddOption<T>(T defaultValue)
        {
            return new Option<T>(OptionChanged, defaultValue);
        }

        public virtual string Name
        {
            get
            {
                string name = GetType().Name;
                name = name.Replace("Options", "");
                return name.SplitWordsByUppercase();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OptionChanged()
        {
            OnPropertyChanged();
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}