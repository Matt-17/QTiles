using System.Configuration;
using System.Data;
using System.Windows;

namespace QTiles;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private string? lastUnhandledMessage;
    private DateTime lastUnhandledShownUtc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Last-resort net for UI-thread exceptions (e.g. from async void handlers)
        // so an unexpected error shows a dialog instead of killing the process.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;

            // A fault in a per-frame path (pan/zoom redraw) would otherwise re-spawn
            // the modal dialog on every tick; show repeats of the same error at most
            // every few seconds.
            var message = args.Exception.Message;
            var now = DateTime.UtcNow;
            if (message == lastUnhandledMessage && (now - lastUnhandledShownUtc) < TimeSpan.FromSeconds(5))
            {
                return;
            }

            lastUnhandledMessage = message;
            lastUnhandledShownUtc = now;
            MessageBox.Show(
                message,
                "Unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
    }
}

