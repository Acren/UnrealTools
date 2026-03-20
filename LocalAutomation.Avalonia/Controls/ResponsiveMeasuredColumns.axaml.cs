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
        if (sender is not Control { DataContext: MeasuredItem { Item: { } item } })
        {
            return;
        }

        if (!_itemHeights.TryGetValue(item, out double currentHeight) || Math.Abs(currentHeight - e.NewSize.Height) >= 1)
        {
            _itemHeights[item] = e.NewSize.Height;
            QueueLayoutUpdate();
        }
    }

    /// <summary>
    /// Tracks the latest available width so rebalancing can follow resize changes without mutating collections during
    /// the active layout pass.
    /// </summary>
    private void HandleSizeChanged(object? sender, SizeChangedEventArgs e)
    {
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
        int columnCount = ComputeColumnCount(usableWidth);
        double cardWidth = ComputeCardWidth(usableWidth, columnCount);

        EnsureColumnCount(columnCount);
        foreach (MeasuredColumn column in Columns)
        {
            column.Reset(cardWidth);
        }

        foreach (object item in items)
        {
            MeasuredColumn targetColumn = Columns.OrderBy(column => column.TotalMeasuredHeight).First();
            targetColumn.AddItem(item, GetMeasuredHeight(item), RowSpacing);
        }

        for (int index = Columns.Count - 1; index >= 0; index--)
        {
            Columns[index].TrailingMargin = index == Columns.Count - 1 ? new Thickness(0) : new Thickness(0, 0, ColumnSpacing, 0);
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
    private int ComputeColumnCount(double usableWidth)
    {
        if (usableWidth <= 0)
        {
            return 1;
        }

        int computedColumns = (int)Math.Floor((usableWidth + ColumnSpacing) / (MinimumItemWidth + ColumnSpacing));
        return Math.Clamp(computedColumns, 1, Math.Max(1, MaxColumns));
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

        return Math.Max(MinimumItemWidth, (usableWidth / Math.Max(1, columnCount)) - ColumnSpacing);
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
    public sealed class MeasuredColumn
    {
        /// <summary>
        /// Gets the items currently assigned to this column.
        /// </summary>
        public AvaloniaList<MeasuredItem> Items { get; } = new();

        /// <summary>
        /// Gets the current card width for this column.
        /// </summary>
        public double CardWidth { get; private set; }

        /// <summary>
        /// Gets the trailing margin applied to this column.
        /// </summary>
        public Thickness TrailingMargin { get; set; }

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
        public void AddItem(object item, double measuredHeight, double rowSpacing)
        {
            if (Items.Count > 0)
            {
                TotalMeasuredHeight += rowSpacing;
            }

            Items.Add(new MeasuredItem(item));
            TotalMeasuredHeight += measuredHeight;
        }
    }

    /// <summary>
    /// Wraps a source item so the control can keep generic measurement metadata separate from consumer data.
    /// </summary>
    public sealed class MeasuredItem
    {
        /// <summary>
        /// Initializes a wrapped measured item.
        /// </summary>
        public MeasuredItem(object item)
        {
            Item = item;
        }

        /// <summary>
        /// Gets the original source item.
        /// </summary>
        public object Item { get; }
    }
}
