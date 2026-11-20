// Minimal signature stubs of WPF / OpenCvSharp / WinRT / Ports / Management used ONLY to type-check the console code on Linux.
#nullable enable
#pragma warning disable CS0067, CS8618, CS0649
namespace System.Windows
{
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public class RoutedEventArgs : EventArgs { public bool Handled { get; set; } }
    public class StartupEventArgs : EventArgs { public string[] Args => Array.Empty<string>(); }
    public enum Visibility { Visible, Hidden, Collapsed }
    public enum WindowState { Normal, Minimized, Maximized }
    public enum WindowStyle { None, SingleBorderWindow, ThreeDBorderWindow, ToolWindow }
    public enum ThemeMode { None, Light, Dark, System }
    public struct Thickness { public Thickness(double u) { } public Thickness(double l, double t, double r, double b) { } }
    public struct Point { public double X { get; set; } public double Y { get; set; } public Point(double x, double y) { X = x; Y = y; } }
    public struct Int32Rect { public Int32Rect(int x, int y, int w, int h) { } }
    public enum GridUnitType { Auto, Pixel, Star }
    public struct GridLength { public GridLength(double v) { } }
    public enum MessageBoxButton { OK, OKCancel, YesNoCancel, YesNo }
    public enum MessageBoxImage { None, Error, Question, Warning, Information }
    public enum MessageBoxResult { None, OK, Cancel, Yes, No }
    public static class MessageBox
    {
        public static MessageBoxResult Show(string text) => default;
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton b, MessageBoxImage i) => default;
        public static MessageBoxResult Show(Window owner, string text) => default;
        public static MessageBoxResult Show(Window owner, string text, string caption) => default;
        public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton b) => default;
        public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton b, MessageBoxImage i) => default;
    }
    public static class Clipboard { public static bool ContainsText() => false; public static string GetText() => ""; public static void SetText(string t) { } }
    public class DependencyObject { public System.Windows.Threading.Dispatcher Dispatcher { get; } = null!; }
    public class UIElement : System.Windows.Media.Visual, System.Windows.Input.IInputElement
    {
        public Visibility Visibility { get; set; }
        public bool IsEnabled { get; set; }
        public bool Focus() => true;
        public bool CaptureMouse() => true;
        public void ReleaseMouseCapture() { }
        public bool IsMouseCaptured { get; }
        public void Measure(Size s) { } public void Arrange(Rect r) { }
    }
    public class FrameworkElement : UIElement
    {
        public double Width { get; set; } public double Height { get; set; }
        public double ActualWidth { get; } public double ActualHeight { get; }
        public bool IsLoaded { get; }
        public object? Tag { get; set; }
        public object? ToolTip { get; set; }
        public Thickness Margin { get; set; }
        public System.Windows.Input.Cursor? Cursor { get; set; }
        public object FindResource(object key) => null!;
        public event RoutedEventHandler? Loaded;
    }
    public class ResourceDictionary { }
    public class Window : System.Windows.Controls.ContentControl
    {
        public string Title { get; set; } = "";
        public WindowState WindowState { get; set; }
        public WindowStyle WindowStyle { get; set; }
        public bool IsActive { get; }
        public Window? Owner { get; set; }
        public bool? DialogResult { get; set; }
        public bool? ShowDialog() => null;
        public WindowStartupLocation WindowStartupLocation { get; set; }
        public event EventHandler? Deactivated;
        public event System.ComponentModel.CancelEventHandler? Closing;
    }
    public class Application : DependencyObject
    {
        public ThemeMode ThemeMode { get; set; }
        protected virtual void OnStartup(StartupEventArgs e) { }
        public event System.Windows.Threading.DispatcherUnhandledExceptionEventHandler? DispatcherUnhandledException;
    }
}
namespace System.Windows.Threading
{
    public enum DispatcherPriority { Render, Normal, Background }
    public class DispatcherOperation { public Task Task => Task.CompletedTask; }
    public class DispatcherOperation<T> : DispatcherOperation { public new Task<T> Task => null!; }
    public class Dispatcher
    {
        public DispatcherOperation BeginInvoke(Delegate method, params object[] args) => null!;
        public DispatcherOperation BeginInvoke(DispatcherPriority p, Delegate method) => null!;
        public DispatcherOperation InvokeAsync(Action a) => null!;
        public DispatcherOperation<T> InvokeAsync<T>(Func<T> f) => null!;
    }
    public class DispatcherTimer { public TimeSpan Interval { get; set; } public event EventHandler? Tick; public void Start() { } public void Stop() { } }
    public class DispatcherUnhandledExceptionEventArgs : EventArgs { public Exception Exception => null!; public bool Handled { get; set; } }
    public delegate void DispatcherUnhandledExceptionEventHandler(object sender, DispatcherUnhandledExceptionEventArgs e);
}
namespace System.Windows.Interop { public class WindowInteropHelper { public WindowInteropHelper(Window w) { } public IntPtr Handle => IntPtr.Zero; } }
namespace System.Windows.Input
{
    public enum MouseButton { Left, Middle, Right, XButton1, XButton2 }
    public enum MouseButtonState { Released, Pressed }
    public class Cursor { }
    public static class Cursors { public static Cursor None => null!; }
    public class MouseEventArgs : RoutedEventArgs
    {
        public MouseButtonState LeftButton { get; } public MouseButtonState RightButton { get; } public MouseButtonState MiddleButton { get; }
        public Point GetPosition(IInputElement? relativeTo) => default;
    }
    public interface IInputElement { }
    public class MouseButtonEventArgs : MouseEventArgs { public MouseButton ChangedButton { get; } }
    public class MouseWheelEventArgs : MouseEventArgs { public int Delta { get; } }
}
namespace System.Windows.Media
{
    public struct Color { public static Color FromRgb(byte r, byte g, byte b) => default; }
    public class Brush { }
    public class SolidColorBrush : Brush { public SolidColorBrush(Color c) { } }
    public class ImageSource { }
    public static class PixelFormats { public static PixelFormat Bgr24 => default; public static PixelFormat Pbgra32 => default; }
    public struct PixelFormat { }
}
namespace System.Windows.Media.Imaging
{
    public class BitmapSource : System.Windows.Media.ImageSource { public int PixelWidth { get; } public int PixelHeight { get; } }
    public class BitmapPalette { }
    public class WriteableBitmap : BitmapSource
    {
        public WriteableBitmap(int w, int h, double dx, double dy, System.Windows.Media.PixelFormat f, BitmapPalette? p) { }
        public void WritePixels(Int32Rect r, Array pixels, int stride, int offset) { }
        public WriteableBitmap Clone() => this;
    }
    public class BitmapFrame : BitmapSource { public static BitmapFrame Create(BitmapSource s) => null!; }
    public class PngBitmapEncoder { public IList<BitmapFrame> Frames { get; } = new List<BitmapFrame>(); public void Save(System.IO.Stream s) { } }
}
namespace System.Windows.Shapes { public class Ellipse : FrameworkElement { public System.Windows.Media.Brush? Fill { get; set; } } }
namespace System.Windows.Documents { public class Run { } }
namespace System.Windows.Controls
{
    public class SelectionChangedEventArgs : RoutedEventArgs { }
    public class Control : FrameworkElement
    {
        public System.Windows.Media.Brush? Foreground { get; set; }
        public System.Windows.Media.Brush? Background { get; set; }
        public Thickness Padding { get; set; }
        public double FontSize { get; set; }
        public bool Focusable { get; set; }
    }
    public class ContentControl : Control { public object? Content { get; set; } }
    public class ButtonBase : ContentControl { public event RoutedEventHandler? Click; }
    public class Button : ButtonBase { public double MinWidth { get; set; } }
    public class ToggleButton : ButtonBase { public bool? IsChecked { get; set; } }
    public class CheckBox : ToggleButton { }
    public class RadioButton : ToggleButton { }
    public class ItemCollection : List<object> { public new void Insert(int i, object o) => base.Insert(i, o); }
    public class ItemsControl : Control { public ItemCollection Items { get; } = new(); }
    public class Selector : ItemsControl { public object? SelectedItem { get; set; } public int SelectedIndex { get; set; } }
    public class ComboBox : Selector { public string Text { get; set; } = ""; public bool IsEditable { get; set; } }
    public class ListBox : Selector { }
    public class TabControl : Selector { }
    public class TextBox : Control { public string Text { get; set; } = ""; }
    public class PasswordBox : Control { public string Password { get; set; } = ""; }
    public class TextBlock : FrameworkElement { public string Text { get; set; } = ""; public System.Windows.Media.Brush? Foreground { get; set; } }
    public class Panel : FrameworkElement { public List<UIElement> Children { get; } = new(); }
    public class StackPanel : Panel { public Orientation Orientation { get; set; } }
    public class WrapPanel : Panel { }
    public class Grid : Panel { }
    public class Border : FrameworkElement { public System.Windows.Media.Brush? Background { get; set; } }
    public class Image : FrameworkElement { public System.Windows.Media.ImageSource? Source { get; set; } public System.Windows.Media.Stretch Stretch { get; set; } }
    public class ProgressBar : Control { public double Value { get; set; } }
    public class ColumnDefinition { public GridLength Width { get; set; } }
}
namespace Microsoft.Win32 { public class OpenFileDialog { public string Filter { get; set; } = ""; public string Title { get; set; } = ""; public string FileName => ""; public bool? ShowDialog(System.Windows.Window owner) => null; } }
namespace System.Windows
{
    public struct Size { public Size(double w, double h) { } }
    public struct Rect { public Rect(double x, double y, double w, double h) { } }
    public enum WindowStartupLocation { Manual, CenterScreen, CenterOwner }
}
namespace System.Windows.Ink
{
    public class DrawingAttributes { public System.Windows.Media.Color Color { get; set; } public double Width { get; set; } public double Height { get; set; } public bool IsHighlighter { get; set; } public bool FitToCurve { get; set; } }
    public class Stroke { }
    public class StrokeCollection : List<Stroke> { }
}
namespace System.Windows.Media
{
    public static class Brushes { public static SolidColorBrush Transparent => null!; }
    public static class Colors { public static Color Red => default; public static Color Yellow => default; }
    public enum Stretch { None, Fill, Uniform, UniformToFill }
    public enum StretchDirection { UpOnly, DownOnly, Both }
    public class Visual : System.Windows.DependencyObject { }
}
namespace System.Windows.Media.Imaging
{
    public enum BitmapCacheOption { Default, OnDemand, OnLoad, None }
    public class BitmapImage : BitmapSource { public void BeginInit() { } public void EndInit() { } public BitmapCacheOption CacheOption { get; set; } public Uri? UriSource { get; set; } }
    public class RenderTargetBitmap : BitmapSource { public RenderTargetBitmap(int w, int h, double dx, double dy, System.Windows.Media.PixelFormat f) { } public void Render(System.Windows.Media.Visual v) { } }
}
namespace System.Windows.Controls
{
    public enum Orientation { Horizontal, Vertical }
    public enum Dock { Left, Top, Right, Bottom }
    public enum ScrollBarVisibility { Disabled, Auto, Hidden, Visible }
    public class InkCanvas : FrameworkElement
    {
        public System.Windows.Media.Brush? Background { get; set; }
        public double Width { get; set; } public double Height { get; set; }
        public System.Windows.Ink.StrokeCollection Strokes { get; } = new();
        public System.Windows.Ink.DrawingAttributes DefaultDrawingAttributes { get; set; } = new();
    }
    public class DockPanel : Panel { public static void SetDock(UIElement e, Dock d) { } }
    public class Viewbox : FrameworkElement { public UIElement? Child { get; set; } public System.Windows.Media.Stretch Stretch { get; set; } public System.Windows.Media.StretchDirection StretchDirection { get; set; } }
    public class ScrollViewer : ContentControl { public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; } public ScrollBarVisibility VerticalScrollBarVisibility { get; set; } }
    public class Expander : ContentControl { }
}
