using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace PhoneBridge;

/// <summary>
/// One line-icon on a 24 unit grid. Stroke follows the surrounding text colour,
/// so an icon always matches the label next to it.
/// </summary>
public class LineIcon : Viewbox
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(LineIcon), new PropertyMetadata(null));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public LineIcon()
    {
        Width = 18;
        Height = 18;
        IsHitTestVisible = false;
        Focusable = false;

        var path = new ShapePath
        {
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        path.SetBinding(ShapePath.DataProperty, new Binding(nameof(Data)) { Source = this });
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding
        {
            Path = new PropertyPath(TextElement.ForegroundProperty),
            RelativeSource = RelativeSource.Self
        });

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        Child = canvas;
    }
}

/// <summary>Lookups into Theme.xaml so code never hard-codes a colour.</summary>
public static class Theme
{
    public static Brush Paint(string key) => (Brush)Application.Current.FindResource(key);
    public static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);
    public static Geometry Geo(string key) => (Geometry)Application.Current.FindResource(key);
    public static FontFamily Font(string key) => (FontFamily)Application.Current.FindResource(key);

    /// <summary>False when the user turned animations off in Windows.</summary>
    public static bool Motion => SystemParameters.ClientAreaAnimation;

    public static string Initial(string text)
    {
        foreach (char c in text)
        {
            if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
            if (char.IsDigit(c) || c == '+') return "#";
        }
        return "#";
    }
}
