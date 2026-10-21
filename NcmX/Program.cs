using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace NcmX;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupDiagnostics.Write($"Entry point; version={typeof(Program).Assembly.GetName().Version}; base={AppContext.BaseDirectory}; OS={Environment.OSVersion}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupDiagnostics.Write("Unhandled CLR exception: " + e.ExceptionObject);
        try {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            StartupDiagnostics.Write("Starting WinUI dispatcher");
            Application.Start(parameters => {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            return 0;
        } catch (Exception ex) {
            StartupDiagnostics.Fatal("WinUI 초기화", ex);
            return 1;
        }
    }
}
