using System.Diagnostics;
using System.Text;

namespace AutoInstallAll.Services.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, TimeSpan Duration)
{
    public bool Success => !TimedOut && ExitCode == 0;
}

/// <summary>Chạy tiến trình con với timeout + CancellationToken, tự kill cả cây tiến trình khi quá hạn.</summary>
public static class ProcessRunner
{
    public static string SystemExe(string name) => Path.Combine(Environment.SystemDirectory, name);

    public static string PowerShellExe =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct,
        string? workingDirectory = null,
        bool captureOutput = true,
        Action<string>? onOutputLine = null,
        Encoding? outputEncoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(fileName)) ?? Environment.SystemDirectory
        };
        if (captureOutput)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = outputEncoding ?? Encoding.UTF8;
            psi.StandardErrorEncoding = outputEncoding ?? Encoding.UTF8;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (captureOutput)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stdout) stdout.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderr) stderr.AppendLine(e.Data);
            };
        }

        var sw = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException($"Không khởi chạy được tiến trình: {fileName}");

        if (captureOutput)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested)
                throw;
            string so, se;
            lock (stdout) so = stdout.ToString();
            lock (stderr) se = stderr.ToString();
            return new ProcessResult(-1, so, se, TimedOut: true, sw.Elapsed);
        }

        string outText, errText;
        lock (stdout) outText = stdout.ToString();
        lock (stderr) errText = stderr.ToString();
        return new ProcessResult(process.ExitCode, outText, errText, TimedOut: false, sw.Elapsed);
    }

    /// <summary>
    /// Chạy Windows PowerShell 5.1 với script truyền qua -EncodedCommand (không lỗi trích dẫn),
    /// output ép UTF-8, $ErrorActionPreference = Stop.
    /// </summary>
    public static Task<ProcessResult> RunPowerShellAsync(
        string script,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? onOutputLine = null)
    {
        // Bọc try/catch: lỗi ghi ra stderr dạng văn bản thường (mặc định -EncodedCommand ghi lỗi dạng CLIXML khó đọc).
        var full = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
                   "$ProgressPreference = 'SilentlyContinue'\n" +
                   "$ErrorActionPreference = 'Stop'\n" +
                   "try {\n" + script + "\n} catch {\n" +
                   "  [Console]::Error.WriteLine($_.Exception.Message)\n" +
                   "  exit 1\n" +
                   "}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync(PowerShellExe,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            timeout, ct, Environment.SystemDirectory, captureOutput: true, onOutputLine, Encoding.UTF8);
    }

    /// <summary>Thoát chuỗi cho tham số dòng lệnh Windows (CommandLineToArgvW).</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0)
            return value;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Thoát chuỗi đặt trong dấu nháy đơn của PowerShell.</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Tiến trình đã tự thoát giữa chừng.
        }
    }
}
