using System.Windows;
using HHT.Bridge.Core.Settings;
using System.Windows.Threading;

namespace HHT.Bridge.TechConsole;

public partial class App : Application
{
    /// <summary>Options passed on the command line (e.g. by the HHC-TechTool Bridge Box tab).</summary>
    public static LaunchOptions Launch { get; private set; } = LaunchOptions.Parse(Array.Empty<string>());

    protected override void OnStartup(StartupEventArgs e)
    {
        Launch = LaunchOptions.Parse(e.Args);
        // Applied after App.xaml resources load and before MainWindow is created (StartupUri),
        // so every control picks up the Windows 11 Fluent dark styles.
#pragma warning disable WPF0001 // Fluent ThemeMode is flagged experimental in .NET 9/10.
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "HHT Bridge Console — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
