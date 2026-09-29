using System.Windows;
using System.Windows.Threading;

namespace MediaSorter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep a genuinely unexpected UI failure visible instead of vanishing silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}",
            "Media Sorter",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
