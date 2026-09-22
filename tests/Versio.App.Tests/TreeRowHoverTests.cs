using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Pointing at a row lights that row, not every folder above it.
/// </summary>
/// <remarks>
/// Seen in the running explorer on 2026-09-21: the pointer over an object eight levels deep,
/// and a staircase of hover fills on all eight ancestors that read like a multi-selection.
/// </remarks>
[Collection("theme")]
public sealed class TreeRowHoverTests
{
    [Fact]
    public void Hover_lights_the_row_under_the_mouse_and_not_its_ancestors()
    {
        // IsMouseOver on a TreeViewItem is true for every ancestor of the hovered row, because
        // an item contains its children. Pointer state cannot be faked in a test, so this
        // checks the one thing that decides it: the hover trigger is sourced from the row's
        // own strip, not from the item.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["TreeRowItem"]);
            var template = Assert.IsType<ControlTemplate>(
                style.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value);

            var hover = template.Triggers.OfType<Trigger>()
                .Single(t => t.Property == UIElement.IsMouseOverProperty);

            Assert.Equal("Row", hover.SourceName);

            // And the row it names is the header strip, not the whole item with its children.
            var item = new TreeViewItem { Style = style, Header = "parent" };
            item.Items.Add(new TreeViewItem { Style = style, Header = "child" });
            var host = new TreeView();
            host.Items.Add(item);
            host.Measure(new Size(300, 200));
            host.Arrange(new Rect(0, 0, 300, 200));
            host.UpdateLayout();

            var row = Assert.IsAssignableFrom<Grid>(item.Template.FindName("Row", item));
            Assert.Equal(0, Grid.GetRow(row));
        });
    }
}
