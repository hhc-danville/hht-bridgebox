using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HHT.Bridge.TechConsole;

/// <summary>Draw on a screenshot (red pen / yellow highlighter), then save a flattened copy for the report.</summary>
public sealed class AnnotateWindow : Window
{
    private readonly string _source;
    private readonly InkCanvas _ink;
    private readonly Grid _surface;
    public string? SavedPath { get; private set; }

    public AnnotateWindow(string imagePath)
    {
        _source = imagePath;
        Title = "Annotate screenshot";
        Width = 1200;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(imagePath);
        bmp.EndInit();

        _ink = new InkCanvas { Background = Brushes.Transparent, Width = bmp.PixelWidth, Height = bmp.PixelHeight };
        SetPen(Colors.Red, 4, highlighter: false);
        _surface = new Grid { Width = bmp.PixelWidth, Height = bmp.PixelHeight };
        _surface.Children.Add(new Image { Source = bmp, Stretch = Stretch.None });
        _surface.Children.Add(_ink);

        var pen = new Button { Content = "Red pen", Margin = new Thickness(0, 0, 6, 0) };
        pen.Click += (_, _) => SetPen(Colors.Red, 4, false);
        var hl = new Button { Content = "Highlighter", Margin = new Thickness(0, 0, 6, 0) };
        hl.Click += (_, _) => SetPen(Colors.Yellow, 18, true);
        var undo = new Button { Content = "Undo", Margin = new Thickness(0, 0, 6, 0) };
        undo.Click += (_, _) => { if (_ink.Strokes.Count > 0) _ink.Strokes.RemoveAt(_ink.Strokes.Count - 1); };
        var save = new Button { Content = "Save", Margin = new Thickness(0, 0, 6, 0) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => { DialogResult = false; };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        foreach (var b in new[] { pen, hl, undo, save, cancel }) bar.Children.Add(b);

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(new ScrollViewer
        {
            Content = new Viewbox { Child = _surface, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Content = dock;
    }

    private void SetPen(Color color, double size, bool highlighter)
    {
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = color, Width = size, Height = size, IsHighlighter = highlighter, FitToCurve = true };
    }

    private void Save()
    {
        var rtb = new RenderTargetBitmap((int)_surface.Width, (int)_surface.Height, 96, 96, PixelFormats.Pbgra32);
        _surface.Measure(new Size(_surface.Width, _surface.Height));
        _surface.Arrange(new Rect(0, 0, _surface.Width, _surface.Height));
        rtb.Render(_surface);
        var path = Path.Combine(Path.GetDirectoryName(_source)!, Path.GetFileNameWithoutExtension(_source) + "-annotated.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        SavedPath = path;
        DialogResult = true;
    }
}
