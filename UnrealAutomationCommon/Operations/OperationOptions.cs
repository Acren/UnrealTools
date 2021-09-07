using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Annotations;

namespace UnrealAutomationCommon.Operations
{
    public class Option<T> : INotifyPropertyChanged
    {
        private T _value;

        public T Value
        {
            get => _value;
            set
            {
                _value = value;
                OnPropertyChanged();
            }
        }

        public Option(Action changedCallback, T defaultValue)
        {
            _value = defaultValue;
            PropertyChanged += (sender, args) => changedCallback();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public static implicit operator T(Option<T> option) => option.Value;
    }

    public class OperationOptions : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        //protected Option<T> AddOption<T>()
        //{
        //    Option<T> option = new Option<T>();
        //    option.PropertyChanged += (sender, args) =>
        //    {
        //        OnPropertyChanged();
        //    };
        //    return option;
        //}

        public virtual string Name
        {
            get
            {
                string name = GetType().Name;
                name = name.Replace("Options", "");
                return name.SplitWordsByUppercase();
            }
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

    }
}
