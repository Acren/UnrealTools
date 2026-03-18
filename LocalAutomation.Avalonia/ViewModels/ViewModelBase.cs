using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Provides a minimal observable base type for the Avalonia parity shell view models.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <summary>
    /// Raised whenever a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Updates a backing field and raises change notifications only when the value actually changes.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Raises a property changed notification for the provided property name.
    /// </summary>
    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
