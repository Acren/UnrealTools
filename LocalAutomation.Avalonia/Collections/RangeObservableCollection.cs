using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace LocalAutomation.Avalonia.Collections;

/// <summary>
/// Provides an observable collection that can raise one consolidated add event for a batch append.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Appends the provided items and raises one collection notification so bursty log output does not translate into
    /// one UI collection update per entry.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        if (items == null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        List<T> materializedItems = items as List<T> ?? items.ToList();
        if (materializedItems.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        int startIndex = Count;
        foreach (T item in materializedItems)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, materializedItems, startIndex));
    }
}
