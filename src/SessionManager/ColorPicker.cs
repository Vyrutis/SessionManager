using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SessionManager;

/// <summary>
/// A compact HSV color picker: saturation/brightness square, hue bar and a hex box.
/// <see cref="ColorChanged"/> fires while dragging (final = false) and once more when the mouse is released (final = true).
/// </summary>
public sealed class ColorPicker : StackPanel
{
    private const double AreaHeight = 120, HueHeight = 14;

    private double _h, _s = 1, _v = 1;
    private readonly Border _hueFill;
    private readonly Grid _area;
    private readonly Canvas _areaMarks;
    private readonly Ellipse _areaMark;
    private readonly Grid _hueBar;
    private readonly Canvas _hueMarks;
    private readonly Border _hueMark;
    private readonly Border _preview;
    private readonly TextBox _hex;
    private bool _updatingHex;

    public event Action<Color, bool>? ColorChanged;

    public ColorPicker()
    {
        // Saturation (left → right) and brightness (top → bottom) over the current hue.
        _hueFill = new Border();
        var white = new Rectangle
        {
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)),
        };
        var black = new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)),
        };
        _areaMark = new Ellipse { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        _areaMark.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.7 };
        _areaMarks = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        _areaMarks.Children.Add(_areaMark);
        var layers = new Grid();
        layers.Children.Add(_hueFill);
        layers.Children.Add(white);
        layers.Children.Add(black);
        layers.SizeChanged += (_, _) => ClipRounded(layers, 6);
        _area = new Grid { Height = AreaHeight, Cursor = Cursors.Cross, Background = Brushes.Transparent };
        _area.Children.Add(layers);
        _area.Children.Add(_areaMarks);
        _area.SizeChanged += (_, _) => PlaceMarks();
        Hook(_area, p => { _s = Math.Clamp(p.X / _area.ActualWidth, 0, 1); _v = 1 - Math.Clamp(p.Y / _area.ActualHeight, 0, 1); });
        Children.Add(_area);

        // Hue bar
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(Theme.FromHsv(i * 60, 1, 1), i / 6.0));
        _hueMark = new Border
        {
            Width = 6, Height = HueHeight + 4, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.7 },
        };
        _hueMarks = new Canvas { IsHitTestVisible = false };
        _hueMarks.Children.Add(_hueMark);
        _hueBar = new Grid { Height = HueHeight, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        _hueBar.Children.Add(new Border { Background = rainbow, CornerRadius = new CornerRadius(HueHeight / 2) });
        _hueBar.Children.Add(_hueMarks);
        _hueBar.SizeChanged += (_, _) => PlaceMarks();
        Hook(_hueBar, p => _h = Math.Clamp(p.X / _hueBar.ActualWidth, 0, 0.9999) * 360);
        Children.Add(_hueBar);

        // Preview + hex
        _preview = new Border { Width = 34, Height = 24, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        _preview.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _hex = new TextBox
        {
            Width = 92, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), MaxLength = 7, ToolTip = "Hex color, e.g. #3B82F6. Press Enter to apply.",
        };
        _hex.TextChanged += (_, _) =>
        {
            if (_updatingHex || Theme.ParseOrNull(_hex.Text) is not { } c) return;
            SetColor(c, updateHex: false);
            ColorChanged?.Invoke(c, false);
        };
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (Theme.ParseOrNull(_hex.Text) is { } c) ColorChanged?.Invoke(c, true);
            e.Handled = true;
        };
        _hex.LostKeyboardFocus += (_, _) =>
        {
            if (Theme.ParseOrNull(_hex.Text) is { } c) ColorChanged?.Invoke(c, true);
            else UpdateHex();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(_preview);
        row.Children.Add(_hex);
        Children.Add(row);

        SetColor(Theme.Parse("#3B82F6"));
    }

    public Color Color => Theme.FromHsv(_h, _s, _v);

    public void SetColor(Color c) => SetColor(c, updateHex: true);

    private void SetColor(Color c, bool updateHex)
    {
        var (h, s, v) = Theme.ToHsv(c);
        if (s > 0.001 && v > 0.001) _h = h; // keep the hue when the color is grey or black
        _s = s;
        _v = v;
        Refresh(updateHex);
    }

    private void Hook(FrameworkElement surface, Action<Point> set)
    {
        void Move(MouseEventArgs e)
        {
            set(e.GetPosition(surface));
            Refresh(updateHex: true);
            ColorChanged?.Invoke(Color, false);
        }
        surface.MouseLeftButtonDown += (_, e) => { surface.CaptureMouse(); Move(e); e.Handled = true; };
        surface.MouseMove += (_, e) => { if (surface.IsMouseCaptured) Move(e); };
        surface.MouseLeftButtonUp += (_, e) =>
        {
            if (!surface.IsMouseCaptured) return;
            surface.ReleaseMouseCapture();
            ColorChanged?.Invoke(Color, true);
            e.Handled = true;
        };
    }

    private void Refresh(bool updateHex)
    {
        _hueFill.Background = new SolidColorBrush(Theme.FromHsv(_h, 1, 1));
        _preview.Background = new SolidColorBrush(Color);
        if (updateHex) UpdateHex();
        PlaceMarks();
    }

    private void UpdateHex()
    {
        _updatingHex = true;
        _hex.Text = Theme.Hex(Color);
        _updatingHex = false;
    }

    private void PlaceMarks()
    {
        Canvas.SetLeft(_areaMark, _s * _area.ActualWidth - _areaMark.Width / 2);
        Canvas.SetTop(_areaMark, (1 - _v) * _area.ActualHeight - _areaMark.Height / 2);
        Canvas.SetLeft(_hueMark, _h / 360 * _hueBar.ActualWidth - _hueMark.Width / 2);
        Canvas.SetTop(_hueMark, -2);
    }

    private static void ClipRounded(FrameworkElement e, double radius) =>
        e.Clip = new RectangleGeometry(new Rect(0, 0, e.ActualWidth, e.ActualHeight), radius, radius);
}
