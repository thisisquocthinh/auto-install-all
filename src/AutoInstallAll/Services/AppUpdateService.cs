using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AutoInstallAll.Models;
using AutoInstallAll.Services.Infrastructure;

namespace AutoInstallAll.Services;

/// <summary>Kết quả kiểm tra bản mới của chính Auto Install All trên GitHub Releases.</summary>
public sealed record AppUpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string TagName,
    string ReleaseUrl,
    string? Notes,
    string? ZipUrl,
    string? ZipName,
    string? ChecksumUrl)
{
    public bool IsNewer => LatestVersion > CurrentVersion;
}

/// <summary>
/// "Kiểm tra cập nhật" trong trang Cài đặt: đọc Release mới nhất của repo, tải gói ZIP win-x64 về .\Updates\,
/// đối chiếu SHA-256 với file .sha256 đính kèm, rồi thay AutoInstallAll.exe sau khi ứng dụng đóng
/// (manifest.json, .\Installers\, .\Icons\, .\Logs\ giữ nguyên).
/// </summary>
public sealed class AppUpdateService
{
    public const string Repository = "thisisquocthinh/auto-install-all";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string Category = "selfupdate";
    private const string ExeName = "AutoInstallAll.exe";

    private readonly StoragePathManager _paths;
    private readonly HttpDownloader _http;
    private readonly LogService _log;

    public AppUpdateService(StoragePathManager paths, HttpDownloader http, LogService log)
    {
        _paths = paths;
        _http = http;
        _log = log;
    }

    public static Version CurrentVersion
    {
        get
        {
#if DEBUG
            // Chỉ bản Debug: giả lập phiên bản cũ để thử luồng cập nhật với Release thật trên GitHub.
            if (Environment.GetEnvironmentVariable("AIA_FAKE_VERSION") is { } fake && Version.TryParse(fake, out var fv))
                return fv;
#endif
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public string UpdatesDirectory => Path.Combine(_paths.RootDirectory, "Updates");

    public async Task<AppUpdateInfo> CheckAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct,
            ("Accept", "application/vnd.github+json"), ("X-GitHub-Api-Version", "2022-11-28")).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Release thiếu tag_name.");
        var latest = ParseVersion(tag) ?? throw new InvalidDataException($"Không đọc được số phiên bản từ tag '{tag}'.");

        string? zipUrl = null, zipName = null, sumUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) { zipUrl = url; zipName = name; }
        }
        if (zipName is not null)
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
                if (string.Equals(asset.GetProperty("name").GetString(), zipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                    sumUrl = asset.GetProperty("browser_download_url").GetString();

        var info = new AppUpdateInfo(CurrentVersion, latest, tag,
            root.GetProperty("html_url").GetString() ?? ReleasesPage,
            root.TryGetProperty("body", out var body) ? body.GetString() : null,
            zipUrl, zipName, sumUrl);
        _log.Info(Category, $"Đang chạy v{info.CurrentVersion}, bản mới nhất trên GitHub {tag}" +
                            (info.IsNewer ? " → có bản mới." : " → đã là bản mới nhất."));
        return info;
    }

    /// <summary>
    /// Tải + xác minh + giải nén AutoInstallAll.exe mới vào .\Updates\, tạo script thay file chạy sau khi ứng dụng thoát.
    /// Trả đường dẫn script; gọi <see cref="LaunchUpdater"/> rồi đóng ứng dụng để hoàn tất.
    /// </summary>
    public async Task<string> PrepareAsync(AppUpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.ZipUrl is null || info.ZipName is null)
            throw new InvalidOperationException("Bản phát hành này không có gói ZIP win-x64 — hãy tải thủ công trên GitHub.");
        if (info.ChecksumUrl is null)
            throw new InvalidOperationException("Bản phát hành thiếu file .sha256 nên không thể xác minh — hãy tải thủ công trên GitHub.");

        Directory.CreateDirectory(UpdatesDirectory);
        var zipPath = Path.Combine(UpdatesDirectory, info.ZipName);
        var partPath = zipPath + ".downloading";

        var sumText = await _http.GetStringAsync(info.ChecksumUrl, ct).ConfigureAwait(false);
        var expected = sumText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();
        if (expected is null || expected.Length != 64)
            throw new InvalidDataException("File .sha256 không hợp lệ.");

        _log.Info(Category, $"Tải {info.ZipName} → {UpdatesDirectory}");
        var result = await _http.DownloadAsync(info.ZipUrl, partPath, progress, TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        if (!string.Equals(result.Sha256, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            throw new InvalidDataException($"SHA-256 không khớp (mong đợi {expected}, nhận {result.Sha256}). Đã huỷ cập nhật.");
        }
        File.Move(partPath, zipPath, overwrite: true);
        _log.Info(Category, $"SHA-256 khớp: {expected}");

        var newExe = Path.Combine(UpdatesDirectory, ExeName + ".new");
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/" + ExeName, StringComparison.OrdinalIgnoreCase)
                                                        || string.Equals(e.FullName, ExeName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"Gói ZIP không chứa {ExeName}.");
            entry.ExtractToFile(newExe, overwrite: true);
        }

        var currentExe = Environment.ProcessPath ?? Path.Combine(_paths.RootDirectory, ExeName);
        var script = Path.Combine(UpdatesDirectory, "apply_update.cmd");
        var backup = Path.Combine(UpdatesDirectory, ExeName + ".old");
        int pid = Environment.ProcessId;
        // Chờ tiến trình hiện tại thoát → giữ bản cũ làm .old → chép bản mới → mở lại; lỗi thì khôi phục bản cũ.
        var cmd = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("chcp 65001 >nul")
            .AppendLine("title Auto Install All - Dang cap nhat...")
            .AppendLine(":wait")
            .AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)")
            .AppendLine($"copy /y \"{currentExe}\" \"{backup}\" >nul")
            .AppendLine($"copy /y \"{newExe}\" \"{currentExe}\" >nul || (copy /y \"{backup}\" \"{currentExe}\" >nul & echo Cap nhat that bai. & pause & exit /b 1)")
            .AppendLine($"del \"{newExe}\" >nul 2>&1")
            .AppendLine($"del \"{zipPath}\" >nul 2>&1")
            .AppendLine($"start \"\" \"{currentExe}\"")
            .AppendLine("exit /b 0")
            .ToString();
        await File.WriteAllTextAsync(script, cmd, new UTF8Encoding(false), ct).ConfigureAwait(false);
        _log.Info(Category, $"Đã sẵn sàng cập nhật lên {info.TagName}.");
        return script;
    }

    /// <summary>Gọi khi khởi động: bản mới đã chạy được → xoá bản sao lưu .old và file tạm của lần cập nhật trước.</summary>
    public static void CleanupAfterUpdate(string rootDirectory)
    {
        var dir = Path.Combine(rootDirectory, "Updates");
        if (!Directory.Exists(dir)) return;
        foreach (var name in new[] { ExeName + ".old", ExeName + ".new", "apply_update.cmd" })
        {
            try { File.Delete(Path.Combine(dir, name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void LaunchUpdater(string script)
    {
        Process.Start(new ProcessStartInfo(ProcessRunner.SystemExe("cmd.exe"), $"/c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = UpdatesDirectory
        })?.Dispose();
        _log.Info(Category, "Đã khởi chạy script cập nhật — ứng dụng sẽ đóng.");
    }

    /// <summary>"v1.0.1", "1.0.1", "v1.2" → Version(1,0,1) / (1,2,0).</summary>
    public static Version? ParseVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(['-', '+', ' ']);
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}
