using System.Collections.ObjectModel;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Groups option cards into a single visual column so the options area can flow top-to-bottom without forcing every
/// card in the same row to inherit the tallest neighbor's height.
/// </summary>
public sealed class OptionColumnViewModel : ViewModelBase
{
    private double _cardWidth;

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
}
