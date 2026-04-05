using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Displays variable-height cards in responsive columns that rebalance by measured height without mutating children
/// during Avalonia's active layout pass.
/// </summary>
public partial class ResponsiveMeasuredColumns : UserControl
{
    private readonly Dictionary<object, double> _itemHeights = new(ReferenceEqualityComparer.Instance);
    private INotifyCollectionChanged? _observedCollection;
    private bool _layoutUpdateQueued;
    private double _lastMeasuredWidth = -1;
    private double _pendingAvailableWidth;

    /// <summary>
    /// Defines the source items distributed across the responsive columns.
    /// </summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ResponsiveMeasuredColumns, IEnumerable?>(nameof(ItemsSource));

    /// <summary>
    /// Defines the template used to render each source item.
    /// </summary>
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<ResponsiveMeasuredColumns, IDataTemplate?>(nameof(ItemTemplate));

    /// <summary>
    /// Defines the minimum width assigned to a card before another column is allowed.
    /// </summary>
    public static readonly StyledProperty<double> MinimumItemWidthProperty =
        AvaloniaProperty.Register<ResponsiveMeasuredColumns, double>(nameof(MinimumItemWidth), 340);

    /// <summary>
    /// Defines the maximum number of columns the layout may create.
    /// </summary>
    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<ResponsiveMeasuredColumns, int>(nameof(MaxColumns), 3);

    /// <summary>
    /// Defines the spacing between columns.
    /// </summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ResponsiveMeasuredColumns, double>(nameof(ColumnSpacing), 10);

    /// <summary>
    /// Defines the spacing between items in the same column.
    /// </summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ResponsiveMeasuredColumns, double>(nameof(RowSpacing), 10);

    /// <summary>
    /// Defines the horizontal padding already consumed by the surrounding viewport.
    /// </summary>
    public static readonly StyledProperty<double> ViewportHorizontalPaddingProperty =
        AvaloniaProperty.Register<ResponsiveMeasuredColumns, double>(nameof(ViewportHorizontalPadding), 0);

    /// <summary>
    /// Initializes the responsive measured-column control.
    /// </summary>
    public ResponsiveMeasuredColumns()
    {
        InitializeComponent();
        SizeChanged += HandleSizeChanged;

        ItemsSourceProperty.Changed.AddClassHandler<ResponsiveMeasuredColumns>((control, _) => control.HandleItemsSourceChanged());
        MinimumItemWidthProperty.Changed.AddClassHandler<ResponsiveMeasuredColumns>((control, _) => control.QueueLayoutUpdate());
        MaxColumnsProperty.Changed.AddClassHandler<ResponsiveMeasuredColumns>((control, _) => control.QueueLayoutUpdate());
        ColumnSpacingProperty.Changed.AddClassHandler<ResponsiveMeasuredColumns>((control, _) => control.QueueLayoutUpdate());
        RowSpacingProperty.Changed.AddClassHandler<ResponsiveMeasuredColumns>((control, _) => control.QueueLayoutUpdate());
        ViewportHorizontalPaddingProperty.Changed.AddClassHandler<ResponsiveMeasuredColumns>((control, _) => control.QueueLayoutUpdate());
    }

    /// <summary>
    /// Gets or sets the source items distributed across the responsive columns.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Gets or sets the template used to render each source item.
    /// </summary>
    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum width assigned to a card before another column is allowed.
    /// </summary>
    public double MinimumItemWidth
    {
        get => GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the maximum number of columns the layout may create.
    /// </summary>
    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>
    /// Gets or sets the spacing between columns.
    /// </summary>
    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets the spacing between items in the same column.
    /// </summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets the horizontal padding already consumed by the surrounding viewport.
    /// </summary>
    public double ViewportHorizontalPadding
    {
        get => GetValue(ViewportHorizontalPaddingProperty);
        set => SetValue(ViewportHorizontalPaddingProperty, value);
    }

    /// <summary>
    /// Gets the current balanced columns rendered by the control.
    /// </summary>
    public AvaloniaList<MeasuredColumn> Columns { get; } = new();

    /// <summary>
    /// Records measured item heights from the realized item hosts so later balancing uses real rendered sizes.
    /// </summary>
    private void ItemHost_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Control { DataContext: MeasuredItemPlacement { Item: { } item } })
        {
            return;
        }

        bool isFirstMeasurement = !_itemHeights.TryGetValue(item, out double currentHeight);
        if (isFirstMeasurement || Math.Abs(currentHeight - e.NewSize.Height) >= 1)
        {
            _itemHeights[item] = e.NewSize.Height;

            // Changing an editor value can temporarily alter measured height while the focused control is mid-edit.
            // Rebalancing immediately causes the card to hop between columns and recreate controls, which drops focus.
            // We only rebalance on first realization; later height changes are cached and picked up the next time a
            // width or collection change legitimately triggers a fresh layout pass.
            if (isFirstMeasurement)
            {
                QueueLayoutUpdate();
            }
        }
    }

    /// <summary>
    /// Tracks the latest available width so rebalancing can follow resize changes without mutating collections during
    /// the active layout pass.
    /// </summary>
    private void HandleSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_lastMeasuredWidth >= 0 && Math.Abs(_lastMeasuredWidth - e.NewSize.Width) < 1)
        {
            return;
        }

        _lastMeasuredWidth = e.NewSize.Width;
        _pendingAvailableWidth = e.NewSize.Width;
        QueueLayoutUpdate();
    }

    /// <summary>
    /// Rewires collection-change notifications when the item source changes so column balancing stays up to date.
    /// </summary>
    private void HandleItemsSourceChanged()
    {
        if (_observedCollection != null)
        {
            _observedCollection.CollectionChanged -= HandleItemsCollectionChanged;
        }

        _observedCollection = ItemsSource as INotifyCollectionChanged;
        if (_observedCollection != null)
        {
            _observedCollection.CollectionChanged += HandleItemsCollectionChanged;
        }

        QueueLayoutUpdate();
    }

    /// <summary>
    /// Rebuilds the balanced columns when the source collection changes.
    /// </summary>
    private void HandleItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueLayoutUpdate();
    }

    /// <summary>
    /// Coalesces repeated resize and measurement updates into one rebalance pass.
    /// </summary>
    private void QueueLayoutUpdate()
    {
        if (_layoutUpdateQueued)
        {
            return;
        }

        _layoutUpdateQueued = true;
        Dispatcher.UIThread.Post(ApplyLayoutUpdate, DispatcherPriority.Background);
    }

    /// <summary>
    /// Rebuilds the responsive columns using the latest width and measured item heights.
    /// </summary>
    private void ApplyLayoutUpdate()
    {
        _layoutUpdateQueued = false;

        List<object> items = ItemsSource?.Cast<object>().ToList() ?? new List<object>();
        TrimStaleMeasurements(items);

        if (items.Count == 0)
        {
            Columns.Clear();
            return;
        }

        double usableWidth = Math.Max(0, _pendingAvailableWidth - ViewportHorizontalPadding);
        int columnCount = ComputeColumnCount(usableWidth, items.Count);
        double cardWidth = ComputeCardWidth(usableWidth, columnCount);

        EnsureColumnCount(columnCount);
        foreach (MeasuredColumn column in Columns)
        {
            column.Reset(cardWidth);
        }

        // Balance taller cards first so large option groups claim space early, then let shorter cards fill the
        // remaining gaps. This avoids a late tall card being forced into an already crowded column just because its
        // source-order turn arrived after smaller cards had already claimed the other columns.
        List<MeasuredItemPlacement> placements = items
            .Select((item, index) => new MeasuredItemPlacement(item, index, GetMeasuredHeight(item)))
            .OrderByDescending(placement => placement.MeasuredHeight)
            .ThenBy(placement => placement.SourceIndex)
            .ToList();

        foreach (MeasuredItemPlacement placement in placements)
        {
            MeasuredColumn targetColumn = Columns.OrderBy(column => column.TotalMeasuredHeight).First();
            targetColumn.AddItem(placement, RowSpacing);
        }

        // After balancing by measured height, restore source ordering within each column so scanning still feels
        // stable and predictable relative to the original option-set order.
        foreach (MeasuredColumn column in Columns)
        {
            column.SortBySourceOrder();
        }

    }

    /// <summary>
    /// Removes cached measurements for items that are no longer present in the source collection.
    /// </summary>
    private void TrimStaleMeasurements(IEnumerable<object> liveItems)
    {
        HashSet<object> liveSet = new(liveItems, ReferenceEqualityComparer.Instance);
        foreach (object staleItem in _itemHeights.Keys.Where(item => !liveSet.Contains(item)).ToList())
        {
            _itemHeights.Remove(staleItem);
        }
    }

    /// <summary>
    /// Computes how many columns can fit within the current available width.
    /// </summary>
    private int ComputeColumnCount(double usableWidth, int itemCount)
    {
        int maxAllowedColumns = Math.Max(1, Math.Min(MaxColumns, itemCount));

        if (usableWidth <= 0)
        {
            return 1;
        }

        int computedColumns = (int)Math.Floor((usableWidth + ColumnSpacing) / (MinimumItemWidth + ColumnSpacing));
        return Math.Clamp(computedColumns, 1, maxAllowedColumns);
    }

    /// <summary>
    /// Computes the width assigned to each card for the current column count.
    /// </summary>
    private double ComputeCardWidth(double usableWidth, int columnCount)
    {
        if (usableWidth <= 0)
        {
            return MinimumItemWidth;
        }

        // Once the column count is chosen from the minimum-width threshold, the remaining available width should be
        // divided evenly across those columns. We floor the result so fractional pixel widths do not round up across
        // multiple columns and clip the right edge of the final card at certain viewport sizes.
        double totalSpacing = Math.Max(0, columnCount - 1) * ColumnSpacing;
        return Math.Max(0, Math.Floor((usableWidth - totalSpacing) / Math.Max(1, columnCount)));
    }

    /// <summary>
    /// Ensures the realized columns collection matches the requested count.
    /// </summary>
    private void EnsureColumnCount(int columnCount)
    {
        while (Columns.Count < columnCount)
        {
            Columns.Add(new MeasuredColumn());
        }

        while (Columns.Count > columnCount)
        {
            Columns.RemoveAt(Columns.Count - 1);
        }
    }

    /// <summary>
    /// Returns the last measured height for an item, falling back to a conservative default until the UI realizes it.
    /// </summary>
    private double GetMeasuredHeight(object item)
    {
        return _itemHeights.TryGetValue(item, out double height) ? height : 120;
    }

    /// <summary>
    /// Groups a vertical stack of items assigned to one balanced column.
    /// </summary>
    public sealed class MeasuredColumn : ViewModelBase
    {
        private double _cardWidth;

        /// <summary>
        /// Gets the items currently assigned to this column.
        /// </summary>
        public AvaloniaList<MeasuredItemPlacement> Items { get; } = new();

        /// <summary>
        /// Gets the current card width for this column.
        /// </summary>
        public double CardWidth
        {
            get => _cardWidth;
            private set => SetProperty(ref _cardWidth, value);
        }

        /// <summary>
        /// Gets the cumulative measured height assigned to this column.
        /// </summary>
        public double TotalMeasuredHeight { get; private set; }

        /// <summary>
        /// Clears the column before the next balancing pass.
        /// </summary>
        public void Reset(double cardWidth)
        {
            CardWidth = cardWidth;
            TotalMeasuredHeight = 0;
            Items.Clear();
        }

        /// <summary>
        /// Appends an item and updates the running measured height for this column.
        /// </summary>
        public void AddItem(MeasuredItemPlacement item, double rowSpacing)
        {
            if (Items.Count > 0)
            {
                TotalMeasuredHeight += rowSpacing;
            }

            Items.Add(item);
            TotalMeasuredHeight += item.MeasuredHeight;
        }

        /// <summary>
        /// Restores source ordering within the column after the balancing pass has decided which column should own
        /// each card.
        /// </summary>
        public void SortBySourceOrder()
        {
            List<MeasuredItemPlacement> sortedItems = Items.OrderBy(item => item.SourceIndex).ToList();
            Items.Clear();

            foreach (MeasuredItemPlacement item in sortedItems)
            {
                Items.Add(item);
            }
        }
    }

    /// <summary>
    /// Wraps a source item so the control can keep generic measurement metadata separate from consumer data.
    /// </summary>
    public sealed class MeasuredItemPlacement
    {
        /// <summary>
        /// Initializes a wrapped measured item.
        /// </summary>
        public MeasuredItemPlacement(object item, int sourceIndex, double measuredHeight)
        {
            Item = item;
            SourceIndex = sourceIndex;
            MeasuredHeight = measuredHeight;
        }

        /// <summary>
        /// Gets the original source item.
        /// </summary>
        public object Item { get; }

        /// <summary>
        /// Gets the original index from the bound item source.
        /// </summary>
        public int SourceIndex { get; }

        /// <summary>
        /// Gets the measured rendered height used during balancing.
        /// </summary>
        public double MeasuredHeight { get; }
    }
}
