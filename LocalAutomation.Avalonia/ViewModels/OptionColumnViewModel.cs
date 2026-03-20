using System.Collections.ObjectModel;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Groups option cards into a single visual column so the options area can flow top-to-bottom without forcing every
/// card in the same row to inherit the tallest neighbor's height.
/// </summary>
public sealed class OptionColumnViewModel : ViewModelBase
{
    private double _cardWidth;
    private double _totalMeasuredHeight;

    /// <summary>
    /// Gets or sets the width assigned to cards within this visual column.
    /// </summary>
    public double CardWidth
    {
        get => _cardWidth;
        set => SetProperty(ref _cardWidth, value);
    }

    /// <summary>
    /// Gets the cards currently assigned to this visual column.
    /// </summary>
    public ObservableCollection<OptionSetViewModel> Items { get; } = new();

    /// <summary>
    /// Gets the cumulative measured height assigned to this column during layout balancing.
    /// </summary>
    public double TotalMeasuredHeight
    {
        get => _totalMeasuredHeight;
        private set => SetProperty(ref _totalMeasuredHeight, value);
    }

    /// <summary>
    /// Clears the column before a fresh redistribution pass.
    /// </summary>
    public void Reset(double cardWidth)
    {
        CardWidth = cardWidth;
        TotalMeasuredHeight = 0;
        Items.Clear();
    }

    /// <summary>
    /// Appends a card to this column and tracks its contribution to the running column height.
    /// </summary>
    public void AddItem(OptionSetViewModel item, double verticalSpacing)
    {
        if (Items.Count > 0)
        {
            TotalMeasuredHeight += verticalSpacing;
        }

        Items.Add(item);
        TotalMeasuredHeight += item.MeasuredHeight;
    }
}
