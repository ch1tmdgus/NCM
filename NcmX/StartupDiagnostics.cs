using System.Runtime.InteropServices;

namespace NcmX;

internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NCM_X", "Logs", "startup.log");

    internal static void Write(string message)
    {
        try {
            lock (Gate) {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [PID {Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        } catch { /* Diagnostics must not prevent startup. */ }
    }
    internal static void Fatal(string stage, Exception error)
    {
        Write($"FATAL {stage}{Environment.NewLine}{error}");
        try {
            MessageBoxW(0, $"NCM X 시작 중 오류가 발생했습니다.\n\n단계: {stage}\n{error.GetType().Name}: {error.Message}\n\n오류 기록: {LogPath}\n\n진단 실행.cmd로 실행하면 추가 정보를 수집할 수 있습니다.",
                "NCM X 실행 오류", 0x10);
        } catch { }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
