using Microsoft.UI.Xaml;
namespace NcmX;
public partial class App : Application
{
    private MainWindow? window;
    public App()
    {
        UnhandledException += (_, e) => StartupDiagnostics.Fatal("화면 처리", e.Exception);
        StartupDiagnostics.Write("Loading App.xaml resources");
        InitializeComponent();
        StartupDiagnostics.Write("App.xaml resources loaded");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try {
            StartupDiagnostics.Write("Creating main window");
            window = new MainWindow();
            StartupDiagnostics.Write("Activating main window");
            window.Activate();
            StartupDiagnostics.Write("Main window activated");
        } catch (Exception ex) {
            StartupDiagnostics.Fatal("메인 화면 생성", ex);
            Environment.Exit(1);
        }
    }
}
