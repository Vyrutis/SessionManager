using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace SessionManager;

/// <summary>
/// The palette menu in the title bar: Silver / Midnight, accent color, main color, custom color picker,
/// background picture, interface size and animations. Every change is previewed live through <see cref="Changed"/>.
/// </summary>
public sealed class ThemeMenu : Border
{
    private static readonly (string Name, string Hex)[] Accents =
    {
        ("Blue", "#3B82F6"), ("Sky", "#0EA5E9"), ("Teal", "#14B8A6"), ("Green", "#22C55E"), ("Lime", "#84CC16"), ("Amber", "#F59E0B"),
        ("Orange", "#F97316"), ("Red", "#EF4444"), ("Pink", "#EC4899"), ("Purple", "#8B5CF6"), ("Indigo", "#6366F1"), ("Slate", "#64748B"),
    };

    private static readonly (string Name, string Hex)[] Mains =
    {
        ("Graphite", "#808080"), ("Slate", "#475569"), ("Ocean", "#1E4E6E"), ("Teal", "#245E5E"), ("Forest", "#2F5D3A"), ("Olive", "#5A5A2E"),
        ("Mocha", "#6B4F3A"), ("Wine", "#6E2F3A"), ("Plum", "#5B3A6E"), ("Indigo", "#3A3F7A"),
    };

    private enum Target { None, Accent, Main }

    private readonly Window _owner;
    private readonly Popup _popup;
    private bool _dark;
    private UiSettings _ui = new();
    private bool _syncing;

    private readonly RadioButton _silver, _midnight;
    private readonly SwatchRow _accentRow, _mainRow;
    private readonly Border _pickerCard;
    private readonly TextBlock _pickerTitle;
    private readonly ColorPicker _picker;
    private Target _target;
    private readonly Border _thumb;
    private readonly Button _removePicture;
    private readonly Slider _visibility, _scale;
    private readonly TextBlock _visibilityValue, _scaleValue;
    private readonly CheckBox _blur, _animations;
    private bool _scaleDragging;

    /// <summary>Mode (dark), settings, and whether this is a final value worth saving (false while dragging).</summary>
    public event Action<bool, UiSettings, bool>? Changed;

    public ThemeMenu(Window owner, Popup popup)
    {
        _owner = owner;
        _popup = popup;
        Width = 312;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        SetResourceReference(LayoutTransformProperty, "UiScale");

        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        Child = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

        // Header
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var reset = new Button { Content = "Reset all", Padding = new Thickness(8, 3, 8, 3), ToolTip = "Back to the standard Silver / Midnight look" };
        reset.SetResourceReference(StyleProperty, "ToolButton");
        reset.SetResourceReference(TextElement.ForegroundProperty, "SubTextBrush");
        reset.Click += (_, _) => ResetAll();
        DockPanel.SetDock(reset, Dock.Right);
        header.Children.Add(reset);
        header.Children.Add(new TextBlock { Text = "Appearance", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);

        // Mode
        root.Children.Add(Heading("Mode"));
        var modes = new UniformGrid { Columns = 2 };
        _silver = Segment("", "Silver", "Light");
        _midnight = Segment("", "Midnight", "Dark");
        _silver.Checked += (_, _) => SetMode(false);
        _midnight.Checked += (_, _) => SetMode(true);
        modes.Children.Add(_silver);
        modes.Children.Add(_midnight);
        root.Children.Add(Framed(modes));

        // Accent and main colors
        root.Children.Add(Heading("Accent color"));
        _accentRow = new SwatchRow("accent", Accents, hex => SetColor(Target.Accent, hex), () => OpenPicker(Target.Accent));
        root.Children.Add(_accentRow.Panel);

        root.Children.Add(Heading("Main color"));
        _mainRow = new SwatchRow("main", Mains, hex => SetColor(Target.Main, hex), () => OpenPicker(Target.Main));
        root.Children.Add(_mainRow.Panel);
        root.Children.Add(Note("Tints the window, panels and menus. Text keeps its contrast."));

        // Custom color picker (opens under the swatches)
        _picker = new ColorPicker();
        _picker.ColorChanged += (c, final) =>
        {
            if (_target == Target.None) return;
            var hex = Theme.Hex(c);
            if (_target == Target.Accent) _ui.Accent = hex; else _ui.Main = hex;
            Sync();
            Push(final);
        };
        _pickerTitle = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var closePicker = new Button { Content = Glyph("", 10), Padding = new Thickness(6), ToolTip = "Close the color picker" };
        closePicker.SetResourceReference(StyleProperty, "ToolButton");
        closePicker.Click += (_, _) => ClosePicker();
        var pickerHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(closePicker, Dock.Right);
        pickerHeader.Children.Add(closePicker);
        pickerHeader.Children.Add(_pickerTitle);
        var pickerBody = new StackPanel();
        pickerBody.Children.Add(pickerHeader);
        pickerBody.Children.Add(_picker);
        _pickerCard = new Border
        {
            Child = pickerBody, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
        };
        _pickerCard.SetResourceReference(BackgroundProperty, "InputBrush");
        _pickerCard.SetResourceReference(BorderBrushProperty, "BorderBrush");
        root.Children.Add(_pickerCard);

        // Background picture
        root.Children.Add(Heading("Background picture"));
        _thumb = new Border { Width = 72, Height = 44, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
        _thumb.SetResourceReference(BorderBrushProperty, "BorderBrush");
        var choose = new Button { Content = "Choose picture…", HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += (_, _) => ChoosePicture();
        _removePicture = new Button { Content = "Remove", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        _removePicture.Click += (_, _) => { _ui.BackgroundImage = null; Sync(); Push(true); };
        var pictureButtons = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        pictureButtons.Children.Add(choose);
        pictureButtons.Children.Add(_removePicture);
        var pictureRow = new DockPanel();
        DockPanel.SetDock(_thumb, Dock.Left);
        pictureRow.Children.Add(_thumb);
        pictureRow.Children.Add(pictureButtons);
        root.Children.Add(pictureRow);

        (_visibility, _visibilityValue) = LabeledSlider(root, "Show through", Theme.MinVisibility, Theme.MaxVisibility, 0.05);
        _visibility.ValueChanged += (_, _) =>
        {
            _visibilityValue.Text = $"{_visibility.Value:P0}";
            if (_syncing) return;
            _ui.BackgroundVisibility = _visibility.Value;
            Push(final: !_visibility.IsMouseCaptureWithin);
        };
        _visibility.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Push(true)));
        _blur = new CheckBox { Content = "Blur the picture", Margin = new Thickness(0, 6, 0, 0) };
        _blur.Click += (_, _) => { _ui.BackgroundBlur = _blur.IsChecked == true; Push(true); };
        root.Children.Add(_blur);

        // Interface
        root.Children.Add(Heading("Interface"));
        (_scale, _scaleValue) = LabeledSlider(root, "Size", Theme.MinScale, Theme.MaxScale, 0.05);
        _scale.ValueChanged += (_, _) =>
        {
            _scaleValue.Text = $"{_scale.Value:P0}";
            if (_syncing || _scaleDragging) return;
            _ui.Scale = Math.Round(_scale.Value, 2);
            Push(true);
        };
        // Resizing everything while the thumb is being dragged would move the slider under the mouse; apply on release.
        _scale.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _scaleDragging = true));
        _scale.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _scaleDragging = false;
            _ui.Scale = Math.Round(_scale.Value, 2);
            Push(true);
        }));
        _animations = new CheckBox { Content = "Animations (fade out when closing)", Margin = new Thickness(0, 6, 0, 0) };
        _animations.Click += (_, _) => { _ui.Animations = _animations.IsChecked == true; Push(true); };
        root.Children.Add(_animations);
    }

    public void Load(bool dark, UiSettings ui)
    {
        _dark = dark;
        _ui = ui.Clone();
        Sync();
    }

    // ───────────── Changes ─────────────

    private void Push(bool final) => Changed?.Invoke(_dark, _ui.Clone(), final);

    private void SetMode(bool dark)
    {
        if (_syncing || dark == _dark) return;
        _dark = dark;
        Sync();
        Push(true);
    }

    private void SetColor(Target target, string? hex)
    {
        if (target == Target.Accent) _ui.Accent = hex; else _ui.Main = hex;
        if (_target == target && hex != null && Theme.ParseOrNull(hex) is { } c) _picker.SetColor(c);
        Sync();
        Push(true);
    }

    private void OpenPicker(Target target)
    {
        _target = target;
        var current = target == Target.Accent ? _ui.Accent ?? Theme.DefaultAccent(_dark) : _ui.Main ?? Theme.DefaultMain(_dark);
        _pickerTitle.Text = target == Target.Accent ? "Custom accent color" : "Custom main color";
        _picker.SetColor(Theme.Parse(current));
        _pickerCard.Visibility = Visibility.Visible;
        // Choosing "custom" immediately switches to the picker's color so the ring moves to the custom swatch.
        if (target == Target.Accent) _ui.Accent = Theme.Hex(_picker.Color); else _ui.Main = Theme.Hex(_picker.Color);
        Sync();
        Push(true);
        _pickerCard.BringIntoView();
    }

    private void ClosePicker()
    {
        _target = Target.None;
        _pickerCard.Visibility = Visibility.Collapsed;
    }

    private void ResetAll()
    {
        var keepPicture = _ui.BackgroundImage;
        _ui = new UiSettings { BackgroundImage = null };
        ClosePicker();
        Sync();
        Push(true);
        if (keepPicture != null) Theme.DeleteBackgroundImages();
    }

    private void ChoosePicture()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Choose a background picture"),
            Filter = "Pictures (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        // The file dialog takes the focus, which would otherwise close this menu.
        bool stays = _popup.StaysOpen;
        _popup.StaysOpen = true;
        try
        {
            if (dlg.ShowDialog(_owner) != true) return;
            string stored;
            try { stored = Theme.StoreBackgroundImage(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, $"This picture can't be used.\n\n{ex.Message}", "Background picture", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _ui.BackgroundImage = stored;
            Sync();
            Push(true);
            Theme.DeleteBackgroundImages(keep: stored);
        }
        finally
        {
            _popup.StaysOpen = stays;
            _popup.IsOpen = true;
        }
    }

    /// <summary>Shows the current settings in the controls.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            _silver.IsChecked = !_dark;
            _midnight.IsChecked = _dark;
            _accentRow.Show(_ui.Accent, Theme.DefaultAccent(_dark));
            _mainRow.Show(_ui.Main, Theme.DefaultMain(_dark));

            bool hasPicture = _ui.BackgroundImage != null;
            _thumb.Background = hasPicture && Theme.BackgroundImage != null && _ui.BackgroundImage == Theme.Ui.BackgroundImage
                ? new ImageBrush(Theme.BackgroundImage) { Stretch = Stretch.UniformToFill }
                : hasPicture ? Brushes.Transparent : null;
            if (!hasPicture) _thumb.SetResourceReference(BackgroundProperty, "InputBrush");
            _thumb.Child = hasPicture ? null : new TextBlock
            {
                Text = "None", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Get("SubTextBrush"),
            };
            _removePicture.IsEnabled = hasPicture;
            _visibility.IsEnabled = _blur.IsEnabled = hasPicture;
            _visibility.Value = _ui.BackgroundVisibility;
            _visibilityValue.Text = $"{_ui.BackgroundVisibility:P0}";
            _blur.IsChecked = _ui.BackgroundBlur;

            if (!_scaleDragging) _scale.Value = _ui.Scale;
            _scaleValue.Text = $"{_ui.Scale:P0}";
            _animations.IsChecked = _ui.Animations;
        }
        finally { _syncing = false; }
    }

    /// <summary>The thumbnail is drawn after the picture has been loaded by <see cref="Theme.Apply(bool, UiSettings)"/>.</summary>
    public void RefreshPicture()
    {
        if (_ui.BackgroundImage != null && Theme.BackgroundImage != null)
        {
            _thumb.Background = new ImageBrush(Theme.BackgroundImage) { Stretch = Stretch.UniformToFill };
            _thumb.Child = null;
        }
    }

    // ───────────── Building blocks ─────────────

    private static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 14, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        return t;
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        return t;
    }

    private static TextBlock Glyph(string glyph, double size)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        return t;
    }

    private static Border Framed(UIElement content)
    {
        var b = new Border { Child = content, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        b.SetResourceReference(BackgroundProperty, "InputBrush");
        b.SetResourceReference(BorderBrushProperty, "BorderBrush");
        return b;
    }

    private static RadioButton Segment(string glyph, string text, string tip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var g = Glyph(glyph, 13);
        g.Margin = new Thickness(0, 0, 7, 0);
        content.Children.Add(g);
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var r = new RadioButton { Content = content, GroupName = "ThemeMode", ToolTip = tip };
        r.SetResourceReference(StyleProperty, "Segment");
        return r;
    }

    private static (Slider, TextBlock) LabeledSlider(Panel parent, string label, double min, double max, double step)
    {
        var value = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var name = new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step, TickFrequency = step,
            IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(name);
        row.Children.Add(value);
        row.Children.Add(slider);
        parent.Children.Add(row);
        return (slider, value);
    }

    /// <summary>A wrap of color dots: theme default, presets, and a custom one that opens the picker.</summary>
    private sealed class SwatchRow
    {
        public readonly WrapPanel Panel = new();
        private readonly RadioButton _default;
        private readonly TextBlock _defaultMark;
        private readonly List<(RadioButton Button, string Hex)> _presets = new();
        private readonly RadioButton _custom;
        private readonly TextBlock _customMark;
        private bool _showing;

        private static readonly Brush Rainbow = MakeRainbow();

        public SwatchRow(string group, (string Name, string Hex)[] presets, Action<string?> pick, Action custom)
        {
            _defaultMark = new TextBlock { Text = "A", FontSize = 10, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
            _default = Make(group, "Theme default", _defaultMark);
            _default.Checked += (_, _) => { if (!_showing) pick(null); };
            Panel.Children.Add(_default);

            foreach (var (name, hex) in presets)
            {
                var b = Make(group, name, null);
                b.Background = new SolidColorBrush(Theme.Parse(hex));
                var h = hex;
                b.Checked += (_, _) => { if (!_showing) pick(h); };
                _presets.Add((b, hex));
                Panel.Children.Add(b);
            }

            _customMark = Glyph("", 10);
            _customMark.IsHitTestVisible = false;
            _custom = Make(group, "Custom color…", _customMark);
            _custom.Background = Rainbow;
            // Clicking "custom" always opens the picker, even when it is already the selected one.
            _custom.Click += (_, _) => custom();
            Panel.Children.Add(_custom);
        }

        private static RadioButton Make(string group, string tip, TextBlock? mark)
        {
            var r = new RadioButton { GroupName = "Swatch-" + group, ToolTip = tip, Content = mark };
            r.SetResourceReference(StyleProperty, "Swatch");
            return r;
        }

        public void Show(string? current, string themeDefault)
        {
            _showing = true;
            var def = Theme.Parse(themeDefault);
            _default.Background = new SolidColorBrush(def);
            _defaultMark.Foreground = Theme.Luminance(def) > 0.55 ? Brushes.Black : Brushes.White;

            var preset = current == null ? default : _presets.FirstOrDefault(p => p.Hex.Equals(current, StringComparison.OrdinalIgnoreCase));
            _default.IsChecked = current == null;
            foreach (var (b, hex) in _presets) b.IsChecked = preset.Button == b;
            bool isCustom = current != null && preset.Button == null;
            _custom.IsChecked = isCustom;
            if (isCustom)
            {
                var c = Theme.Parse(current!);
                _custom.Background = new SolidColorBrush(c);
                _customMark.Text = ""; // pencil
                _customMark.Foreground = Theme.Luminance(c) > 0.55 ? Brushes.Black : Brushes.White;
            }
            else
            {
                _custom.Background = Rainbow;
                _customMark.Text = ""; // plus
                _customMark.Foreground = Brushes.White;
            }
            _showing = false;
        }

        private static Brush MakeRainbow()
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            for (int i = 0; i <= 6; i++) b.GradientStops.Add(new GradientStop(Theme.FromHsv(i * 60, 0.75, 0.95), i / 6.0));
            b.Freeze();
            return b;
        }
    }
}
