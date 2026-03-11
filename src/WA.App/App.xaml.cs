using System.Windows;

namespace WA.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Global exception handling
        DispatcherUnhandledException += (s, ex) =>
        {
            var full = ex.Exception.ToString();
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "wa_crash.txt");
            System.IO.File.WriteAllText(logPath, full);
            MessageBox.Show($"Xato log: {logPath}\n\n{full[..Math.Min(600, full.Length)]}",
                "Windows Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
