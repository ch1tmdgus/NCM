using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NcmX;

public sealed class EngineClient : IDisposable
{
    private Process? process;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private bool disposed;
    public event Action<JsonElement>? Message;
    public event Action<string>? Error;
    public event Action? Exited;

    public void Start()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Engine", "NcmEngine.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Engine 폴더가 없습니다. 프로젝트 루트에서 build.ps1을 실행하세요.", exe);
        process = new Process { StartInfo = new ProcessStartInfo(exe) {
            WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true
        }, EnableRaisingEvents = true };
        process.Exited += (_, _) => { if (!disposed) Exited?.Invoke(); };
        process.Start();
        _ = ReadMessages();
        _ = ReadErrors();
    }

    private async Task ReadMessages()
    {
        try {
            while (process != null && await process.StandardOutput.ReadLineAsync() is string line) {
                try {
                    using var json = JsonDocument.Parse(line);
                    Message?.Invoke(json.RootElement.Clone());
                } catch (JsonException) { Error?.Invoke("엔진 응답을 해석하지 못했습니다."); }
            }
        } catch (Exception ex) { if (!disposed) Error?.Invoke(ex.Message); }
    }

    private async Task ReadErrors()
    {
        try {
            while (process != null && await process.StandardError.ReadLineAsync() is string line)
                if (!string.IsNullOrWhiteSpace(line)) Error?.Invoke(line);
        } catch (Exception ex) { if (!disposed) Error?.Invoke(ex.Message); }
    }

    public async Task SendAsync(object command)
    {
        await writeLock.WaitAsync();
        try {
            if (disposed || process == null || process.HasExited) return;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
            await process.StandardInput.FlushAsync();
        } catch (Exception ex) { if (!disposed) Error?.Invoke(ex.Message); }
        finally { writeLock.Release(); }
    }

    public void Dispose()
    {
        disposed = true;
        try {
            if (process != null && !process.HasExited) {
                // Closing stdin asks the engine to stop; terminate the process tree on app exit.
                process.StandardInput.Close();
                if (!process.WaitForExit(400)) process.Kill(entireProcessTree: true);
            }
        } catch (InvalidOperationException) { }
        finally { process?.Dispose(); }
    }
}
