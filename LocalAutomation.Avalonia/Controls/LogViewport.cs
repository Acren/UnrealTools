using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Draws the runtime log directly so very large wrapped lines can be measured and rendered with a lightweight,
/// viewport-driven path instead of asking a generic text control to fully lay out the whole line at once.
/// </summary>
public sealed class LogViewport : Control
{
    private const double FontSize = 12;
    private const double LineHeight = 16;
    private const double HorizontalGap = 8;
    private const double VerticalPadding = 2;
    private const double SelectionAccentWidth = 3;
    private const double SelectionGutterWidth = 8;

    private static readonly FontFamily MonoFontFamily = new("Consolas, Cascadia Mono, monospace");
    private static readonly Typeface MonoTypeface = new(MonoFontFamily);
    private static readonly IBrush TimestampBrush = new SolidColorBrush(Color.Parse("#8B949E"));
    private static readonly IBrush FocusedSelectionFallbackBrush = new SolidColorBrush(Color.Parse("#0C89919A"));
    private static readonly IBrush UnfocusedSelectionFallbackBrush = new SolidColorBrush(Color.Parse("#12AEB9C5"));
    private static readonly IBrush HoverFallbackBrush = new SolidColorBrush(Color.Parse("#20262C"));
    private static readonly IBrush SelectionAccentFallbackBrush = new SolidColorBrush(Color.Parse("#AEB9C5"));
    private static readonly IBrush HitTestBrush = Brushes.Transparent;
    private static readonly IReadOnlyDictionary<string, IBrush> MessageBrushes = new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase)
    {
        ["#E65050"] = new SolidColorBrush(Color.Parse("#E65050")),
        ["#E6E60A"] = new SolidColorBrush(Color.Parse("#E6E60A")),
        ["#E6E6E6"] = new SolidColorBrush(Color.Parse("#E6E6E6"))
    };

    private readonly List<double> _rowHeights = new();
    private IReadOnlyList<LogEntryViewModel>? _entries;
    private INotifyCollectionChanged? _entriesCollection;
    private double _characterWidth;
    private double _timestampColumnWidth;
    private double _cachedLayoutWidth = double.NaN;
    private double _totalHeight;
    private int? _selectionAnchorIndex;
    private int? _selectionStartIndex;
    private int? _selectionEndIndex;
    private int? _hoveredRowIndex;
    private bool _isDraggingSelection;
    private ScrollViewer? _owningScrollViewer;

    /// <summary>
    /// The runtime log entries displayed by the viewport.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<LogEntryViewModel>?> EntriesProperty =
        AvaloniaProperty.Register<LogViewport, IReadOnlyList<LogEntryViewModel>?>(nameof(Entries));

    /// <summary>
    /// Raised whenever the user selection changes so the host can enable copy commands.
    /// </summary>
    public event EventHandler? SelectionChanged;

    static LogViewport()
    {
        AffectsMeasure<LogViewport>(EntriesProperty);
        AffectsRender<LogViewport>(EntriesProperty);
        AffectsRender<LogViewport>(IsFocusedProperty);
        FocusableProperty.OverrideDefaultValue<LogViewport>(true);
    }

    /// <summary>
    /// Initializes the viewport and measures the fixed-width metrics used by the wrap calculator.
    /// </summary>
    public LogViewport()
    {
        InitializeTextMetrics();
    }

    /// <summary>
    /// Gets or sets the log entries rendered by the viewport.
    /// </summary>
    public IReadOnlyList<LogEntryViewModel>? Entries
    {
        get => GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    /// <summary>
    /// Rewires collection listeners and cached row heights when the entry source changes.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EntriesProperty)
        {
            AttachEntries(change.GetNewValue<IReadOnlyList<LogEntryViewModel>?>());
        }
    }

    /// <summary>
    /// Hooks the parent scroll viewer when the viewport enters the visual tree so rendering can use the current scroll
    /// offset to draw only visible rows.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owningScrollViewer = this.FindAncestorOfType<ScrollViewer>();
    }

    /// <summary>
    /// Drops the cached parent scroll viewer when the viewport leaves the visual tree.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _owningScrollViewer = null;
    }

    /// <summary>
    /// Recomputes row heights for the current width and reports the total estimated extent back to the scroll viewer.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double layoutWidth = ResolveLayoutWidth(availableSize.Width);
        EnsureRowHeights(layoutWidth);
        return new Size(layoutWidth, _totalHeight);
    }

    /// <summary>
    /// Renders only the rows that overlap the visible viewport, plus a small overscan margin, so huge logs stay cheap
    /// as long as most entries remain off-screen.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // A transparent fill makes the entire viewport hit-testable so hover/selection work across blank row areas,
        // not only where glyphs were drawn.
        context.DrawRectangle(HitTestBrush, null, new Rect(Bounds.Size));

        IReadOnlyList<LogEntryViewModel>? entries = _entries;
        if (entries == null || entries.Count == 0)
        {
            return;
        }

        double viewportTop = _owningScrollViewer?.Offset.Y ?? 0;
        double viewportHeight = _owningScrollViewer?.Viewport.Height ?? Bounds.Height;
        double viewportBottom = viewportTop + viewportHeight;
        double layoutWidth = Math.Max(Bounds.Width, 1);
        EnsureRowHeights(layoutWidth);

        double drawTop = Math.Max(0, viewportTop - (LineHeight * 4));
        double drawBottom = viewportBottom + (LineHeight * 4);
        (int startIndex, double startY) = FindRowAtOffset(drawTop);
        double rowY = startY;
        for (int index = startIndex; index < entries.Count && rowY <= drawBottom; index++)
        {
            LogEntryViewModel entry = entries[index];
            double rowHeight = _rowHeights[index];
            Rect rowBounds = new(0, rowY, layoutWidth, rowHeight);
            if (rowBounds.Bottom >= drawTop)
            {
                DrawRow(context, rowBounds, entry, index, layoutWidth);
            }

            rowY += rowHeight;
        }
    }

    /// <summary>
    /// Starts a drag-selection gesture anchored on the row under the pointer.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        int? rowIndex = GetRowIndexAtPoint(e.GetPosition(this));
        if (rowIndex == null)
        {
            ClearSelection();
            return;
        }

        _selectionAnchorIndex = rowIndex;
        SetSelection(rowIndex.Value, rowIndex.Value);
        _isDraggingSelection = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <summary>
    /// Extends the drag-selection over any rows crossed by the pointer.
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHoveredRow(e.GetPosition(this));
        if (!_isDraggingSelection || _selectionAnchorIndex == null)
        {
            return;
        }

        int? rowIndex = GetRowIndexAtPoint(e.GetPosition(this));
        if (rowIndex == null)
        {
            return;
        }

        SetSelection(_selectionAnchorIndex.Value, rowIndex.Value);
        e.Handled = true;
    }

    /// <summary>
    /// Completes the drag-selection gesture.
    /// </summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDraggingSelection)
        {
            return;
        }

        _isDraggingSelection = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// Clears the hover preview when the pointer leaves the viewport.
    /// </summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoveredRow(null);
    }

    /// <summary>
    /// Supports copying the currently selected rows with Ctrl+C so row selection remains useful before drag text
    /// selection exists.
    /// </summary>
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers != KeyModifiers.Control || e.Key != Key.C)
        {
            return;
        }

        string selectedText = BuildSelectedText();
        if (string.IsNullOrEmpty(selectedText))
        {
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            await topLevel.Clipboard.SetTextAsync(selectedText);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Subscribes to the new entry collection and rebuilds the height cache for the current width.
    /// </summary>
    private void AttachEntries(IReadOnlyList<LogEntryViewModel>? entries)
    {
        if (_entriesCollection != null)
        {
            _entriesCollection.CollectionChanged -= HandleEntriesCollectionChanged;
            _entriesCollection = null;
        }

        _entries = entries;
        _entriesCollection = entries as INotifyCollectionChanged;
        if (_entriesCollection != null)
        {
            _entriesCollection.CollectionChanged += HandleEntriesCollectionChanged;
        }

        RebuildRowHeights();
    }

    /// <summary>
    /// Keeps the height cache aligned with the append-only log collection so the viewport can extend smoothly as new
    /// lines arrive.
    /// </summary>
    private void HandleEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_entries == null)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            EnsureRowHeights(ResolveLayoutWidth(Bounds.Width));
            foreach (LogEntryViewModel entry in e.NewItems.OfType<LogEntryViewModel>())
            {
                double rowHeight = MeasureEntryHeight(entry, ResolveLayoutWidth(Bounds.Width));
                _rowHeights.Add(rowHeight);
                _totalHeight += rowHeight;
            }

            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        RebuildRowHeights();
    }

    /// <summary>
    /// Recomputes all cached row heights for the current width so wrap metrics stay aligned after resizes or collection
    /// resets.
    /// </summary>
    private void RebuildRowHeights()
    {
        _rowHeights.Clear();
        _totalHeight = 0;
        _cachedLayoutWidth = double.NaN;
        ClampSelection();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Ensures the cached row heights are available for the current width before hit testing or drawing.
    /// </summary>
    private void EnsureRowHeights(double layoutWidth)
    {
        IReadOnlyList<LogEntryViewModel>? entries = _entries;
        if (entries == null)
        {
            _rowHeights.Clear();
            _totalHeight = 0;
            _cachedLayoutWidth = layoutWidth;
            return;
        }

        if (Math.Abs(_cachedLayoutWidth - layoutWidth) < 0.5 && _rowHeights.Count == entries.Count)
        {
            return;
        }

        _rowHeights.Clear();
        _totalHeight = 0;
        foreach (LogEntryViewModel entry in entries)
        {
            double rowHeight = MeasureEntryHeight(entry, layoutWidth);
            _rowHeights.Add(rowHeight);
            _totalHeight += rowHeight;
        }

        _cachedLayoutWidth = layoutWidth;
        ClampSelection();
    }

    /// <summary>
    /// Measures one entry using the same viewport-width-aware wrapping algorithm that the renderer uses, so scroll math
    /// and drawn output stay in sync without creating a giant wrapped text layout object.
    /// </summary>
    private double MeasureEntryHeight(LogEntryViewModel entry, double layoutWidth)
    {
        int wrappedLineCount = WrapEntry(entry, layoutWidth).Count;
        return Math.Max(1, wrappedLineCount) * LineHeight + (VerticalPadding * 2);
    }

    /// <summary>
    /// Draws one selected or unselected row directly from the wrapped line segments.
    /// </summary>
    private void DrawRow(DrawingContext context, Rect rowBounds, LogEntryViewModel entry, int rowIndex, double layoutWidth)
    {
        DrawRowBackground(context, rowBounds, rowIndex);

        IReadOnlyList<string> wrappedLines = WrapEntry(entry, layoutWidth);
        double timestampX = SelectionGutterWidth;
        double messageX = SelectionGutterWidth + _timestampColumnWidth + HorizontalGap;
        double lineY = rowBounds.Top + VerticalPadding;
        for (int lineIndex = 0; lineIndex < wrappedLines.Count; lineIndex++)
        {
            if (lineIndex == 0)
            {
                DrawText(context, $"[{entry.TimestampText}]", TimestampBrush, new Point(timestampX, lineY));
            }

            DrawText(context, wrappedLines[lineIndex], ResolveMessageBrush(entry.Foreground), new Point(messageX, lineY));
            lineY += LineHeight;
        }
    }

    /// <summary>
    /// Wraps one entry into viewport-width-aware display lines using the fixed-width font metrics instead of asking the
    /// text engine to process the whole giant message in a single wrapped layout.
    /// </summary>
    private IReadOnlyList<string> WrapEntry(LogEntryViewModel entry, double layoutWidth)
    {
        double availableMessageWidth = Math.Max(1, layoutWidth - SelectionGutterWidth - _timestampColumnWidth - HorizontalGap);
        int maxCharactersPerLine = Math.Max(1, (int)Math.Floor(availableMessageWidth / _characterWidth));
        List<string> wrappedLines = new();
        string normalizedMessage = entry.Message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (string physicalLine in normalizedMessage.Split('\n'))
        {
            AppendWrappedPhysicalLine(wrappedLines, physicalLine, maxCharactersPerLine);
        }

        if (wrappedLines.Count == 0)
        {
            wrappedLines.Add(string.Empty);
        }

        return wrappedLines;
    }

    /// <summary>
    /// Wraps a single physical line greedily at whitespace boundaries so the viewport behaves like a normal wrapped log
    /// for human-readable output while still handling long unbroken tokens safely.
    /// </summary>
    private static void AppendWrappedPhysicalLine(List<string> wrappedLines, string physicalLine, int maxCharactersPerLine)
    {
        if (string.IsNullOrEmpty(physicalLine))
        {
            wrappedLines.Add(string.Empty);
            return;
        }

        int startIndex = 0;
        while (startIndex < physicalLine.Length)
        {
            int remainingLength = physicalLine.Length - startIndex;
            if (remainingLength <= maxCharactersPerLine)
            {
                wrappedLines.Add(physicalLine.Substring(startIndex));
                return;
            }

            int breakLength = FindWrapLength(physicalLine, startIndex, maxCharactersPerLine);
            wrappedLines.Add(physicalLine.Substring(startIndex, breakLength));
            startIndex += breakLength;
            while (startIndex < physicalLine.Length && char.IsWhiteSpace(physicalLine[startIndex]))
            {
                startIndex++;
            }
        }
    }

    /// <summary>
    /// Prefers a nearby whitespace break, but falls back to a hard boundary for very long uninterrupted tokens.
    /// </summary>
    private static int FindWrapLength(string text, int startIndex, int maxCharactersPerLine)
    {
        int candidateEndIndex = Math.Min(text.Length - 1, startIndex + maxCharactersPerLine);
        int minimumPreferredBreakIndex = startIndex + Math.Max(1, maxCharactersPerLine / 2);
        for (int index = candidateEndIndex; index >= minimumPreferredBreakIndex; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return Math.Max(1, index - startIndex);
            }
        }

        return Math.Max(1, Math.Min(maxCharactersPerLine, text.Length - startIndex));
    }

    /// <summary>
    /// Maps a viewport-relative pointer position to the logical row index under that point.
    /// </summary>
    private int? GetRowIndexAtPoint(Point point)
    {
        if (_entries == null || _entries.Count == 0)
        {
            return null;
        }

        // Pointer coordinates are already reported in the viewport's local content space, so adding the scroll offset a
        // second time shifts hit testing farther down the log the more the user has scrolled.
        EnsureRowHeights(ResolveLayoutWidth(Bounds.Width));
        (int rowIndex, _) = FindRowAtOffset(point.Y);
        return rowIndex >= 0 && rowIndex < _entries.Count ? rowIndex : null;
    }

    /// <summary>
    /// Finds the first row whose bounds intersect the provided vertical offset.
    /// </summary>
    private (int Index, double RowTop) FindRowAtOffset(double offset)
    {
        if (_rowHeights.Count == 0)
        {
            return (0, 0);
        }

        double clampedOffset = Math.Max(0, offset);
        double rowTop = 0;
        for (int index = 0; index < _rowHeights.Count; index++)
        {
            double rowBottom = rowTop + _rowHeights[index];
            if (clampedOffset < rowBottom)
            {
                return (index, rowTop);
            }

            rowTop = rowBottom;
        }

        return (_rowHeights.Count - 1, Math.Max(0, _totalHeight - _rowHeights[^1]));
    }

    /// <summary>
    /// Updates the selected row range and repaints only when the range actually changed.
    /// </summary>
    private void SetSelection(int anchorIndex, int currentIndex)
    {
        int newStart = Math.Min(anchorIndex, currentIndex);
        int newEnd = Math.Max(anchorIndex, currentIndex);
        if (_selectionStartIndex == newStart && _selectionEndIndex == newEnd)
        {
            return;
        }

        _selectionStartIndex = newStart;
        _selectionEndIndex = newEnd;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Clears the current row selection when the user clicks empty space.
    /// </summary>
    private void ClearSelection()
    {
        if (_selectionStartIndex == null && _selectionEndIndex == null)
        {
            return;
        }

        _selectionAnchorIndex = null;
        _selectionStartIndex = null;
        _selectionEndIndex = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Ensures the selection range stays within bounds after collection changes.
    /// </summary>
    private void ClampSelection()
    {
        if (_entries == null || _entries.Count == 0)
        {
            _selectionAnchorIndex = null;
            _selectionStartIndex = null;
            _selectionEndIndex = null;
            return;
        }

        if (_selectionAnchorIndex != null)
        {
            _selectionAnchorIndex = Math.Clamp(_selectionAnchorIndex.Value, 0, _entries.Count - 1);
        }

        if (_selectionStartIndex != null)
        {
            _selectionStartIndex = Math.Clamp(_selectionStartIndex.Value, 0, _entries.Count - 1);
        }

        if (_selectionEndIndex != null)
        {
            _selectionEndIndex = Math.Clamp(_selectionEndIndex.Value, 0, _entries.Count - 1);
        }
    }

    /// <summary>
    /// Returns whether the provided row index falls inside the active drag-selected range.
    /// </summary>
    private bool IsRowSelected(int rowIndex)
    {
        return _selectionStartIndex != null &&
               _selectionEndIndex != null &&
               rowIndex >= _selectionStartIndex.Value &&
               rowIndex <= _selectionEndIndex.Value;
    }

    /// <summary>
    /// Draws the hover and selection chrome for a row with a subtle terminal-style background and a stronger accent
    /// strip when the row is selected.
    /// </summary>
    private void DrawRowBackground(DrawingContext context, Rect rowBounds, int rowIndex)
    {
        Rect highlightBounds = new(SelectionGutterWidth, rowBounds.Y, Math.Max(0, rowBounds.Width - SelectionGutterWidth), rowBounds.Height);
        if (highlightBounds.Width <= 0 || highlightBounds.Height <= 0)
        {
            return;
        }

        if (IsRowSelected(rowIndex))
        {
            IBrush fillBrush = IsFocused
                ? ResolveThemeBrush("GraphiteLineSubtleBrush", FocusedSelectionFallbackBrush)
                : ResolveThemeBrush("GraphiteActiveBackgroundBrush", UnfocusedSelectionFallbackBrush);
            context.DrawRectangle(fillBrush, null, highlightBounds);

            Rect accentBounds = new(
                0,
                rowBounds.Y,
                Math.Min(SelectionAccentWidth, SelectionGutterWidth),
                rowBounds.Height);
            context.DrawRectangle(ResolveThemeBrush("GraphiteAccentBrush", SelectionAccentFallbackBrush), null, accentBounds);
            return;
        }

        if (_hoveredRowIndex == rowIndex)
        {
            context.DrawRectangle(ResolveThemeBrush("GraphiteDropdownHoverBrush", HoverFallbackBrush), null, highlightBounds);
        }
    }

    /// <summary>
    /// Serializes the selected rows back into their original timestamped log lines for clipboard copy.
    /// </summary>
    private string BuildSelectedText()
    {
        if (_entries == null || _selectionStartIndex == null || _selectionEndIndex == null)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        for (int index = _selectionStartIndex.Value; index <= _selectionEndIndex.Value && index < _entries.Count; index++)
        {
            LogEntryViewModel entry = _entries[index];
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append('[');
            builder.Append(entry.TimestampText);
            builder.Append("] ");
            builder.Append(entry.Message);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Draws one short single-line fragment with the viewport's monospaced log typography.
    /// </summary>
    private static void DrawText(DrawingContext context, string text, IBrush brush, Point origin)
    {
        FormattedText formattedText = new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            FontSize,
            brush);
        context.DrawText(formattedText, origin);
    }

    /// <summary>
    /// Returns a stable brush for common severity colors so rendering visible rows does not allocate unnecessary brush
    /// objects.
    /// </summary>
    private static IBrush ResolveMessageBrush(string foreground)
    {
        return MessageBrushes.TryGetValue(foreground, out IBrush? brush)
            ? brush
            : new SolidColorBrush(Color.Parse(foreground));
    }

    /// <summary>
    /// Resolves a brush from the shared graphite resource dictionary so the custom viewport inherits the shell's visual
    /// language instead of introducing a separate hard-coded selection palette.
    /// </summary>
    private IBrush ResolveThemeBrush(string resourceKey, IBrush fallbackBrush)
    {
        if (global::Avalonia.Application.Current?.TryGetResource(resourceKey, ActualThemeVariant, out object? resource) == true && resource is IBrush brush)
        {
            return brush;
        }

        return fallbackBrush;
    }

    /// <summary>
    /// Updates the hovered row from a pointer position so the viewport can show a subtle hover preview distinct from
    /// the stronger selection treatment.
    /// </summary>
    private void UpdateHoveredRow(Point point)
    {
        SetHoveredRow(GetRowIndexAtPoint(point));
    }

    /// <summary>
    /// Stores the hovered row index and repaints only when the hover target changes.
    /// </summary>
    private void SetHoveredRow(int? rowIndex)
    {
        if (_hoveredRowIndex == rowIndex)
        {
            return;
        }

        _hoveredRowIndex = rowIndex;
        InvalidateVisual();
    }

    /// <summary>
    /// Resolves a safe width for wrapping even before the control has been fully arranged.
    /// </summary>
    private double ResolveLayoutWidth(double availableWidth)
    {
        if (!double.IsInfinity(availableWidth) && availableWidth > 0)
        {
            return availableWidth;
        }

        if (Bounds.Width > 0)
        {
            return Bounds.Width;
        }

        return 1;
    }

    /// <summary>
    /// Measures the fixed-width glyph metrics once so the custom wrap algorithm can model viewport width in characters.
    /// </summary>
    private void InitializeTextMetrics()
    {
        FormattedText characterMeasure = new(
            "0",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            FontSize,
            TimestampBrush);
        _characterWidth = Math.Max(1, characterMeasure.WidthIncludingTrailingWhitespace);
        _timestampColumnWidth = Math.Ceiling("[00:00:00]".Length * _characterWidth);
    }
}
