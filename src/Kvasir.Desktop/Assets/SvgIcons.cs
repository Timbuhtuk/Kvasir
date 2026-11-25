using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace Kvasir.Desktop.Assets;

public static class SvgIcons
{
    private static readonly Dictionary<string, (Geometry Geometry, Rect ViewBox)> Cache = new();
    public static Geometry Settings => Load("Settings");
    public static Geometry Load(string name) => LoadAsset(name).Geometry;

    internal static (Geometry Geometry, Rect ViewBox) LoadAsset(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        using var stream = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Kvasir.Desktop;component/Assets/Icons/{name.ToLowerInvariant()}.svg")).Stream;
        var svg = XDocument.Load(stream);
        string path = string.Join(" ", svg.Descendants(XName.Get("path", "http://www.w3.org/2000/svg")).Select(p => p.Attribute("d")!.Value));
        Geometry geometry = Geometry.Parse(path);
        geometry.Freeze();

        Rect viewBox = new(0, 0, 24, 24);
        if (svg.Root?.Attribute("viewBox") is XAttribute bounds)
        {
            var coordinates = bounds.Value.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
            viewBox = new Rect(
                double.Parse(coordinates[0], CultureInfo.InvariantCulture),
                double.Parse(coordinates[1], CultureInfo.InvariantCulture),
                double.Parse(coordinates[2], CultureInfo.InvariantCulture),
                double.Parse(coordinates[3], CultureInfo.InvariantCulture));
        }

        var asset = (geometry, viewBox);
        Cache.Add(name, asset);
        return asset;
    }
}

public sealed class SvgIcon : Viewbox
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(SvgIcon), new PropertyMetadata("Play", Changed));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SvgIcon));
    private readonly Canvas _canvas = new();
    private readonly System.Windows.Shapes.Path _path = new() { StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public SvgIcon()
    {
        Width = 20; Height = 20; IsHitTestVisible = false;
        _canvas.Children.Add(_path); Child = _canvas;
        _path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        SetIcon(Kind);
    }

    private void SetIcon(string kind)
    {
        var (geometry, viewBox) = SvgIcons.LoadAsset(kind);
        _canvas.Width = viewBox.Width;
        _canvas.Height = viewBox.Height;
        Canvas.SetLeft(_path, -viewBox.X);
        Canvas.SetTop(_path, -viewBox.Y);
        _path.Data = geometry;
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SvgIcon)d).SetIcon((string)e.NewValue);
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double seconds = value is double d && double.IsFinite(d) ? Math.Clamp(d, 0, 864000) : 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
