using System.Diagnostics;
using System.IO.Compression;
using System.Net.NetworkInformation;
using AutoInstallAll.Models;
using AutoInstallAll.Services.Infrastructure;

namespace AutoInstallAll.Services;

public sealed record InstallResult(
    SoftwareItem Item,
    bool Success,
    int ExitCode,
    bool RebootRequired,
    string Message,
    TimeSpan Duration);

public sealed record InstallQueueProgress(
    int Index,
    int Total,
    SoftwareItem Current,
    double ItemPercent,
    double OverallPercent,
    string Message);

/// <summary>
/// Hàng đợi cài đặt tuần tự từ .\Installers\:
/// .exe/.msi chạy Silent Switch, .zip Portable giải nén + Shortcut + Task Scheduler,
/// tự tải về USB nếu thiếu bộ cài và máy có mạng.
/// </summary>
public sealed class SilentInstallService
{
    private const string Category = "install";
    private const int MsiBusyExitCode = 1618; // ERROR_INSTALL_ALREADY_RUNNING
    private const int MsiBusyRetries = 5;
    private static readonly TimeSpan MsiBusyDelay = TimeSpan.FromSeconds(20);

    /// <summary>Mã thoát được coi là thành công.</summary>
    private static readonly IReadOnlyDictionary<int, string> SuccessCodes = new Dictionary<int, string>
    {
        [0] = "Thành công",
        [3010] = "Thành công — cần khởi động lại",            // ERROR_SUCCESS_REBOOT_REQUIRED
        [1641] = "Thành công — đã yêu cầu khởi động lại",     // ERROR_SUCCESS_REBOOT_INITIATED
        [1638] = "Đã có phiên bản khác được cài đặt"          // ERROR_PRODUCT_VERSION
    };

    private readonly StoragePathManager _paths;
    private readonly ManifestConfig _manifest;
    private readonly LogService _log;
    private readonly AutoUpdaterService _updater;
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    public event EventHandler<InstallResult>? ItemCompleted;

    public SilentInstallService(StoragePathManager paths, ManifestConfig manifest, LogService log, AutoUpdaterService updater)
    {
        _paths = paths;
        _manifest = manifest;
        _log = log;
        _updater = updater;
    }

    public bool IsRunning => _queueLock.CurrentCount == 0;

    private TimeSpan PerAppTimeout => TimeSpan.FromMinutes(Math.Max(1, _manifest.Settings.InstallTimeoutMinutes));

    // =====================================================================
    // Hàng đợi
    // =====================================================================

    public async Task<IReadOnlyList<InstallResult>> InstallQueueAsync(
        IEnumerable<SoftwareItem> items,
        IProgress<InstallQueueProgress>? progress,
        CancellationToken ct)
    {
        var queue = items.Distinct().ToList();
        if (queue.Count == 0) return Array.Empty<InstallResult>();
        if (!await _queueLock.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Hàng đợi cài đặt đang chạy.");

        var results = new List<InstallResult>();
        var sw = Stopwatch.StartNew();
        try
        {
            _log.Info(Category, $"===== Bắt đầu hàng đợi: {string.Join(", ", queue.Select(i => i.Name))} =====");
            foreach (var item in queue)
            {
                item.State = SoftwareState.Queued;
                item.Progress = 0;
                item.StatusMessage = "Đang chờ";
            }

            for (int i = 0; i < queue.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = queue[i];
                int index = i;
                progress?.Report(new InstallQueueProgress(index, queue.Count, item, 0, index * 100.0 / queue.Count, $"Installing {item.Name} silently..."));

                var itemProgress = new Progress<double>(p =>
                    progress?.Report(new InstallQueueProgress(index, queue.Count, item, p,
                        (index + p / 100) * 100.0 / queue.Count, item.StatusMessage ?? item.Name)));

                results.Add(await InstallAsync(item, itemProgress, ct).ConfigureAwait(false));
            }

            progress?.Report(new InstallQueueProgress(queue.Count, queue.Count, queue[^1], 100, 100, "Hoàn tất"));
            var ok = results.Count(r => r.Success);
            _log.Info(Category, $"===== Kết thúc: {ok}/{results.Count} thành công, {sw.Elapsed:mm\\:ss}. " +
                                $"Cần khởi động lại: {(results.Any(r => r.RebootRequired) ? "Có" : "Không")} =====");
            return results;
        }
        catch (OperationCanceledException)
        {
            foreach (var item in queue.Where(x => x.State == SoftwareState.Queued))
            {
                item.State = _paths.InstallerExists(item) ? SoftwareState.Ready : SoftwareState.Missing;
                item.StatusMessage = "Đã huỷ";
            }
            _log.Warn(Category, $"Hàng đợi bị huỷ sau {results.Count}/{queue.Count} ứng dụng.");
            throw;
        }
        finally
        {
            _queueLock.Release();
        }
    }

    // =====================================================================
    // Một ứng dụng
    // =====================================================================

    public async Task<InstallResult> InstallAsync(SoftwareItem item, IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _log.Info(Category, $"[{item.Name}] Bắt đầu ({item.InstallerType}/{item.PackagerType}, args: {item.SilentArgs})");
        try
        {
            var installerPath = await EnsureInstallerAvailableAsync(item, progress, ct).ConfigureAwait(false);

            item.State = SoftwareState.Installing;
            item.Progress = 0;
            item.StatusMessage = $"Installing {item.Name} silently...";
            progress?.Report(0);

            InstallResult result;
            if (item.InstallerType == InstallerType.Zip || item.PackagerType == PackagerType.Portable)
                result = await InstallPortableZipAsync(item, installerPath, ct).ConfigureAwait(false);
            else if (item.InstallerType == InstallerType.Msi)
                result = await InstallMsiAsync(item, installerPath, ct).ConfigureAwait(false);
            else if (item.PackagerType == PackagerType.DirectXRedist)
                result = await InstallDirectXAsync(item, installerPath, ct).ConfigureAwait(false);
            else
                result = await InstallExeAsync(item, installerPath, ct).ConfigureAwait(false);

            if (result.Success)
                await RunPostInstallAsync(item, ct).ConfigureAwait(false);

            result = result with { Duration = sw.Elapsed };
            item.State = result.Success ? SoftwareState.Installed : SoftwareState.Failed;
            item.Progress = 100;
            item.StatusMessage = result.Message;
            progress?.Report(100);
            if (result.Success)
                _log.Info(Category, $"[{item.Name}] {result.Message} (exit {result.ExitCode}, {sw.Elapsed:mm\\:ss})");
            else
                _log.Error(Category, $"[{item.Name}] {result.Message} (exit {result.ExitCode}, {sw.Elapsed:mm\\:ss})");
            ItemCompleted?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.State = SoftwareState.Failed;
            item.StatusMessage = "Đã huỷ";
            _log.Warn(Category, $"[{item.Name}] Đã huỷ bởi người dùng.");
            throw;
        }
        catch (Exception ex)
        {
            item.State = SoftwareState.Failed;
            item.Progress = 0;
            item.StatusMessage = ex.Message;
            _log.Error(Category, $"[{item.Name}] Lỗi cài đặt", ex);
            var failed = new InstallResult(item, false, -1, false, ex.Message, sw.Elapsed);
            ItemCompleted?.Invoke(this, failed);
            return failed;
        }
    }

    /// <summary>Dùng bộ cài trên USB; nếu thiếu và có mạng thì tải về .\Installers\ trước.</summary>
    private async Task<string> EnsureInstallerAvailableAsync(SoftwareItem item, IProgress<double>? progress, CancellationToken ct)
    {
        if (_paths.InstallerExists(item))
            return _paths.GetInstallerPath(item)!;

        if (!NetworkInterface.GetIsNetworkAvailable())
            throw new FileNotFoundException($"Chưa có bộ cài {item.Name} trong .\\Installers\\ và máy không có mạng để tải.");

        _log.Info(Category, $"[{item.Name}] Không có bộ cài trên USB → tải về .\\Installers\\.");
        item.StatusMessage = "Đang tải bộ cài về USB...";
        // Tải chiếm 0–100% của thẻ trước khi chuyển sang trạng thái Installing.
        return await _updater.DownloadLatestAsync(item, progress, ct).ConfigureAwait(false);
    }

    // ---------------- .exe ----------------

    private async Task<InstallResult> InstallExeAsync(SoftwareItem item, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.SilentArgs))
            _log.Warn(Category, $"[{item.Name}] Không có Silent Switch — bộ cài có thể hiện giao diện.");

        var r = await ProcessRunner.RunAsync(path, item.SilentArgs ?? "", PerAppTimeout, ct,
            workingDirectory: _paths.InstallersDirectory, captureOutput: false).ConfigureAwait(false);
        return Interpret(item, r);
    }

    // ---------------- .msi ----------------

    private async Task<InstallResult> InstallMsiAsync(SoftwareItem item, string path, CancellationToken ct)
    {
        var msiLog = _paths.GetLogPath($"msi_{item.Id}");
        var silent = string.IsNullOrWhiteSpace(item.SilentArgs) ? "/qn /norestart" : item.SilentArgs;
        var args = $"/i {ProcessRunner.Quote(path)} {silent} /L*v+ {ProcessRunner.Quote(msiLog)}";

        ProcessResult r = null!;
        for (int attempt = 1; attempt <= MsiBusyRetries; attempt++)
        {
            r = await ProcessRunner.RunAsync(ProcessRunner.SystemExe("msiexec.exe"), args, PerAppTimeout, ct,
                workingDirectory: _paths.InstallersDirectory, captureOutput: false).ConfigureAwait(false);
            if (r.ExitCode != MsiBusyExitCode) break;

            _log.Warn(Category, $"[{item.Name}] Windows Installer đang bận (1618), thử lại {attempt}/{MsiBusyRetries} sau {MsiBusyDelay.TotalSeconds:0}s.");
            item.StatusMessage = "Windows Installer đang bận, chờ thử lại...";
            await Task.Delay(MsiBusyDelay, ct).ConfigureAwait(false);
        }
        return Interpret(item, r);
    }

    // ---------------- DirectX June 2010 (SFX → DXSETUP) ----------------

    private async Task<InstallResult> InstallDirectXAsync(SoftwareItem item, string path, CancellationToken ct)
    {
        var extractDir = _paths.GetExtractDirectory(item.Id);
        try
        {
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            Directory.CreateDirectory(extractDir);

            item.StatusMessage = "Đang giải nén DirectX redist...";
            var extract = await ProcessRunner.RunAsync(path, $"/Q /T:{ProcessRunner.Quote(extractDir)}", PerAppTimeout, ct,
                captureOutput: false).ConfigureAwait(false);
            if (!extract.Success)
                return Interpret(item, extract) with { Success = false, Message = $"Giải nén DirectX thất bại (exit {extract.ExitCode})" };

            var dxSetup = Path.Combine(extractDir, "DXSETUP.exe");
            if (!File.Exists(dxSetup))
                return new InstallResult(item, false, -1, false, "Không thấy DXSETUP.exe sau khi giải nén.", TimeSpan.Zero);

            item.StatusMessage = "Đang cài DirectX Runtime...";
            var r = await ProcessRunner.RunAsync(dxSetup, string.IsNullOrWhiteSpace(item.SilentArgs) ? "/silent" : item.SilentArgs,
                PerAppTimeout, ct, extractDir, captureOutput: false).ConfigureAwait(false);
            return Interpret(item, r);
        }
        finally
        {
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(Category, $"Không xoá được thư mục giải nén {extractDir}", ex);
            }
        }
    }

    // ---------------- .zip Portable (UniKey) ----------------

    private async Task<InstallResult> InstallPortableZipAsync(SoftwareItem item, string zipPath, CancellationToken ct)
    {
        var post = item.PostInstall;
        var target = Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(post.ExtractTo) ? Path.Combine("%ProgramFiles%", StoragePathManager.SanitizeFileName(item.Name)) : post.ExtractTo);
        target = Path.GetFullPath(target);

        // Đóng phiên bản đang chạy để ghi đè file (UniKeyNT.exe giữ khoá file).
        if (!string.IsNullOrWhiteSpace(post.MainExecutable))
            KillRunning(Path.GetFileNameWithoutExtension(post.MainExecutable), target);

        item.StatusMessage = $"Đang giải nén vào {target}";
        Directory.CreateDirectory(target);
        using (var timeoutCts = new CancellationTokenSource(PerAppTimeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
        {
            await Task.Run(() => ExtractZip(zipPath, target, linked.Token), linked.Token).ConfigureAwait(false);
        }

        var exe = FindMainExecutable(target, post.MainExecutable)
                  ?? throw new FileNotFoundException($"Không tìm thấy file chạy chính ({post.MainExecutable ?? "*.exe"}) sau khi giải nén.");
        _log.Info(Category, $"[{item.Name}] Đã giải nén → {exe}");

        if (post.CreateDesktopShortcut)
            await CreateShortcutsAsync(item, exe).ConfigureAwait(false);

        if (post.RegisterStartupTask)
        {
            await RegisterStartupTaskAsync(item, exe, ct).ConfigureAwait(false);
        }
        else
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        }

        return new InstallResult(item, true, 0, false, $"Đã cài Portable vào {target}", TimeSpan.Zero);
    }

    private static void ExtractZip(string zipPath, string target, CancellationToken ct)
    {
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Mục ZIP không hợp lệ (zip-slip): {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static string? FindMainExecutable(string dir, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
            : Directory.EnumerateFiles(dir, Path.GetFileName(name), SearchOption.AllDirectories).FirstOrDefault();

    private void KillRunning(string processName, string installDir)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    _log.Info(Category, $"Đã đóng {processName} (PID {p.Id}) để ghi đè.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Warn(Category, $"Không đóng được {processName} (PID {p.Id})", ex);
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private async Task CreateShortcutsAsync(SoftwareItem item, string exe)
    {
        var name = StoragePathManager.SanitizeFileName(item.Name) + ".lnk";
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name);
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name);
        await ShellInterop.CreateShortcutAsync(desktop, exe, description: item.Name).ConfigureAwait(false);
        await ShellInterop.CreateShortcutAsync(startMenu, exe, description: item.Name).ConfigureAwait(false);
        _log.Info(Category, $"[{item.Name}] Đã tạo shortcut Desktop + Start Menu.");
    }

    /// <summary>Task Scheduler: chạy khi đăng nhập với quyền cao nhất (UniKey cần Admin để gõ được trong app chạy Admin).</summary>
    private async Task RegisterStartupTaskAsync(SoftwareItem item, string exe, CancellationToken ct)
    {
        var taskName = $"AutoInstallAll\\{StoragePathManager.SanitizeFileName(item.Name)}";
        var create = await ProcessRunner.RunAsync(ProcessRunner.SystemExe("schtasks.exe"),
            $"/Create /TN {ProcessRunner.Quote(taskName)} /TR {ProcessRunner.Quote(ProcessRunner.Quote(exe))} /SC ONLOGON /RL HIGHEST /F",
            TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!create.Success)
            throw new InvalidOperationException($"Không tạo được Task Scheduler (exit {create.ExitCode}): {create.StdErr.Trim()}");

        _log.Info(Category, $"[{item.Name}] Đã đăng ký Task Scheduler '{taskName}' (ONLOGON, HIGHEST).");

        var run = await ProcessRunner.RunAsync(ProcessRunner.SystemExe("schtasks.exe"),
            $"/Run /TN {ProcessRunner.Quote(taskName)}", TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!run.Success)
            _log.Warn(Category, $"[{item.Name}] Không khởi chạy được task ngay: {run.StdErr.Trim()}");
    }

    // ---------------- Hậu cài đặt ----------------

    private async Task RunPostInstallAsync(SoftwareItem item, CancellationToken ct)
    {
        var post = item.PostInstall;
        foreach (var rule in post.CopyFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var from = _paths.ResolveRelative(rule.From);
                if (!File.Exists(from))
                {
                    if (rule.Optional)
                        _log.Info(Category, $"[{item.Name}] Bỏ qua {rule.From} (không có trên USB).");
                    else
                        _log.Warn(Category, $"[{item.Name}] Thiếu file bắt buộc {rule.From}.");
                    continue;
                }
                var to = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rule.To));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
                _log.Info(Category, $"[{item.Name}] Đã chép {rule.From} → {to}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(Category, $"[{item.Name}] Chép {rule.From} thất bại", ex);
            }
        }

        // Shortcut cho app .exe/.msi có khai báo MainExecutable (đường dẫn tuyệt đối, cho phép biến môi trường).
        if (item.InstallerType != InstallerType.Zip && post.CreateDesktopShortcut && !string.IsNullOrWhiteSpace(post.MainExecutable))
        {
            var exe = Environment.ExpandEnvironmentVariables(post.MainExecutable);
            if (Path.IsPathRooted(exe) && File.Exists(exe))
            {
                try { await CreateShortcutsAsync(item, exe).ConfigureAwait(false); }
                catch (Exception ex) { _log.Warn(Category, $"[{item.Name}] Tạo shortcut thất bại", ex); }
            }
        }
    }

    private InstallResult Interpret(SoftwareItem item, ProcessResult r)
    {
        if (r.TimedOut)
            return new InstallResult(item, false, -1, false,
                $"Quá thời gian {PerAppTimeout.TotalMinutes:0} phút — đã dừng tiến trình cài đặt.", r.Duration);

        if (SuccessCodes.TryGetValue(r.ExitCode, out var msg))
            return new InstallResult(item, true, r.ExitCode, r.ExitCode is 3010 or 1641, msg, r.Duration);

        return new InstallResult(item, false, r.ExitCode, false, $"Cài đặt thất bại (exit code {r.ExitCode} / 0x{r.ExitCode:X8})", r.Duration);
    }
}
