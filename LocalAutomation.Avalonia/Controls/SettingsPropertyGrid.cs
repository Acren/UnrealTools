using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.PropertyGrid.Controls;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Adds a second muted line under property labels so settings descriptions stay visible without relying on tooltips.
/// </summary>
public sealed class SettingsPropertyGrid : PropertyGrid
{
    /// <summary>
    /// Creates the customized property grid and replaces name blocks when a property exposes a description.
    /// </summary>
    public SettingsPropertyGrid()
    {
        // The third-party grid already exposes the property descriptor here, so we can keep the customization local to
        // the settings window instead of forking the entire control template.
        CustomNameBlock += HandleCustomNameBlock;
    }

    /// <summary>
    /// Replaces the default one-line property label with a richer label block when a description is available.
    /// </summary>
    private void HandleCustomNameBlock(object? sender, RoutedEventArgs e)
    {
        if (e is not CustomNameBlockEventArgs nameBlockEventArgs
            || nameBlockEventArgs.Context.Property is not PropertyDescriptor descriptor
            || string.IsNullOrWhiteSpace(descriptor.Description))
        {
            return;
        }

        nameBlockEventArgs.CustomNameBlock = BuildNameBlock(descriptor);
    }

    /// <summary>
    /// Builds a two-line label block with the normal display name first and the longer description underneath.
    /// </summary>
    private static Control BuildNameBlock(PropertyDescriptor descriptor)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        panel.Children.Add(new TextBlock
        {
            Text = descriptor.DisplayName,
            Classes = { "property-grid-name" },
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = descriptor.Description,
            Classes = { "property-grid-description" },
            TextWrapping = TextWrapping.Wrap
        });

        return panel;
    }
}
