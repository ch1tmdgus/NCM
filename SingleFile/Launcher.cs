using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("NCM X")]
[assembly: AssemblyProduct("NCM X")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

// Only the distribution wrapper changes. The WinUI app and macro engine are unchanged.
internal static class Launcher
{
    private const string PackageId = "__PACKAGE_ID__";
    private static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NCM_X");
    private static readonly string RuntimeRoot = Path.Combine(AppData, "Runtime");
    private static readonly string Target = Path.Combine(RuntimeRoot, "1.2.0-" + PackageId.Substring(0, 16));
    private static readonly string[] Required = { "NCM_X.exe", "NCM_X.dll", "resources.pri", "App.xbf", "Engine/NcmEngine.exe" };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            Directory.CreateDirectory(RuntimeRoot);
            bool reused;
            using (Mutex gate = new Mutex(false, @"Local\NCM_X_Package_" + PackageId))
            {
                bool acquired = false;
                try
                {
                    try { acquired = gate.WaitOne(TimeSpan.FromMinutes(3)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("다른 NCM X 실행에서 파일을 준비하고 있습니다. 잠시 후 다시 실행하세요.");
                    reused = IsReady();
                    if (!reused) Extract();
                }
                finally { if (acquired) gate.ReleaseMutex(); }
            }
            Log("Runtime " + (reused ? "reused" : "prepared") + "; elapsed_ms=" + timer.ElapsedMilliseconds + "; package=" + PackageId.Substring(0, 16));
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Target, "NCM_X.exe"));
            start.WorkingDirectory = Target;
            start.UseShellExecute = false;
            start.Arguments = JoinArguments(args);
            Process child = Process.Start(start);
            if (child == null) throw new IOException("프로그램을 시작할 수 없습니다.");
            child.Dispose();
            return 0;
        }
        catch (Exception error)
        {
            Log(error.ToString());
            MessageBoxW(IntPtr.Zero, "NCM X 실행 파일을 준비하지 못했습니다.\n\n" + error.Message + "\n\n기록: " + Path.Combine(AppData, "Logs", "launcher.log"), "NCM X", 0x10);
            return 1;
        }
    }

    private static bool IsReady()
    {
        string marker = Path.Combine(Target, ".complete");
        if (!File.Exists(marker) || File.ReadAllText(marker) != PackageId) return false;
        foreach (string file in Required)
        {
            string path = Path.Combine(Target, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        }
        return true;
    }

    private static void Extract()
    {
        string staging = Target + ".preparing-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            Dictionary<string, string> hashes = ReadManifest();
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("NcmPayload"))
            using (ZipArchive zip = new ZipArchive(resource, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count != hashes.Count) throw new InvalidDataException("실행 파일 구성 개수가 다릅니다.");
                string prefix = staging + Path.DirectorySeparatorChar;
                byte[] buffer = new byte[131072];
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string expected;
                    if (!hashes.TryGetValue(entry.FullName, out expected)) throw new InvalidDataException("알 수 없는 실행 파일 구성입니다.");
                    string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("올바르지 않은 파일 경로입니다.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (SHA256 sha = SHA256.Create())
                    using (CryptoStream checkedInput = new CryptoStream(input, sha, CryptoStreamMode.Read))
                    {
                        int read;
                        while ((read = checkedInput.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
                        if (Hex(sha.Hash) != expected) throw new InvalidDataException("실행 파일 무결성 검사에 실패했습니다: " + entry.FullName);
                    }
                }
            }
            File.WriteAllText(Path.Combine(staging, ".complete"), PackageId, Encoding.ASCII);
            if (Directory.Exists(Target)) Directory.Delete(Target, true);
            Directory.Move(staging, Target);
        }
        finally
        {
            if (Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch { } }
        }
    }

    private static Dictionary<string, string> ReadManifest()
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("NcmManifest"))
        using (StreamReader reader = new StreamReader(input, Encoding.UTF8))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ') throw new InvalidDataException("실행 파일 목록이 손상되었습니다.");
                result.Add(line.Substring(66), line.Substring(0, 64));
            }
        }
        return result;
    }

    private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

    // Windows command-line quoting, including empty values and trailing backslashes.
    private static string JoinArguments(string[] args)
    {
        StringBuilder all = new StringBuilder();
        foreach (string arg in args)
        {
            if (all.Length != 0) all.Append(' ');
            all.Append('"');
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { all.Append('\\', slashes * 2 + 1); all.Append(c); }
                else { all.Append('\\', slashes); all.Append(c); }
                slashes = 0;
            }
            all.Append('\\', slashes * 2);
            all.Append('"');
        }
        return all.ToString();
    }

    private static void Log(string message)
    {
        try
        {
            string directory = Path.Combine(AppData, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "launcher.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
