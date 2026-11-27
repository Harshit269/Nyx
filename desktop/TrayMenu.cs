using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PhoneBridge;

/// <summary>
/// The tray icon's right-click menu, drawn in Nyx's own style instead of Windows' plain one:
/// a rounded card with icons, a soft highlight under the pointer and Quit in red.
/// A tiny invisible window holds focus so the menu closes when you click elsewhere.
/// </summary>
public sealed class TrayMenu
{
    readonly Window _host;
    readonly ContextMenu _menu;

    public TrayMenu(IEnumerable<(string Label, Geometry? Icon, bool Danger, Action Run)?> items)
    {
        _host = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, Width = 1, Height = 1, Left = -10000, Top = -10000, ShowActivated = true
        };
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint, Template = MenuTemplate(), HasDropShadow = false };
        foreach (var item in items)
        {
            if (item == null) { _menu.Items.Add(new Separator { Template = SeparatorTemplate() }); continue; }
            var (label, icon, danger, run) = item.Value;
            var mi = new MenuItem { Header = label, Tag = icon, Template = ItemTemplate(danger) };
            mi.Click += (_, _) => run();
            _menu.Items.Add(mi);
        }
        _menu.Closed += (_, _) => _host.Hide();
    }

    public void Show()
    {
        _host.Show();
        _host.Activate();
        _menu.PlacementTarget = _host;
        _menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ looks

    static ControlTemplate MenuTemplate()
    {
        var t = new ControlTemplate(typeof(ContextMenu));
        var card = new FrameworkElementFactory(typeof(Border));
        card.SetResourceReference(Border.BackgroundProperty, "Tile");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        card.SetValue(Border.PaddingProperty, new Thickness(6));
        card.SetValue(FrameworkElement.MarginProperty, new Thickness(10));
        card.SetValue(FrameworkElement.MinWidthProperty, 210.0);
        card.SetValue(UIElement.EffectProperty, new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.4, Direction = 270 });
        var items = new FrameworkElementFactory(typeof(StackPanel));
        items.SetValue(Panel.IsItemsHostProperty, true);
        card.AppendChild(items);
        t.VisualTree = card;
        return t;
    }

    static ControlTemplate ItemTemplate(bool danger)
    {
        var t = new ControlTemplate(typeof(MenuItem));
        var bg = new FrameworkElementFactory(typeof(Border), "Bg");
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        bg.SetValue(Border.PaddingProperty, new Thickness(12, 9, 16, 9));
        bg.SetValue(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand);

        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var icon = new FrameworkElementFactory(typeof(LineIcon));
        icon.SetBinding(LineIcon.DataProperty, new System.Windows.Data.Binding("Tag") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        icon.SetValue(FrameworkElement.WidthProperty, 15.0);
        icon.SetValue(FrameworkElement.HeightProperty, 15.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetResourceReference(TextElement.ForegroundProperty, danger ? "Red" : "Ink");
        row.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Header") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        text.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Red" : "Ink");
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        text.SetValue(TextBlock.FontSizeProperty, 13.5);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(text);

        bg.AppendChild(row);
        t.VisualTree = bg;
        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Tile2"), "Bg"));
        t.Triggers.Add(hover);
        return t;
    }

    static ControlTemplate SeparatorTemplate()
    {
        var t = new ControlTemplate(typeof(Separator));
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.HeightProperty, 1.0);
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 5, 10, 5));
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        t.VisualTree = line;
        return t;
    }
}
