using System.Configuration;
using System.Data;
using System.Windows;

namespace QTiles;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Last-resort net for UI-thread exceptions (e.g. from async void handlers)
        // so an unexpected error shows a dialog instead of killing the process.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.Message,
                "Unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}

